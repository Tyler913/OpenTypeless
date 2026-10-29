import AppKit
import Combine
import os
import TypelessCore
import UniformTypeIdentifiers

/// Transcribes audio and video files into History, one at a time, alongside dictating (see FileTranscription for the
/// rules). Each file becomes a record of its own, marked as a file: it's decoded into the record's recording, then that
/// recording goes through the pipeline a block at a time, and the transcript is cleaned up when asked. Nothing is pasted;
/// the text waits in History. A file is retried from its saved recording like a dictation, reusing what was transcribed.
@MainActor
final class FileTranscriber: ObservableObject {
    /// Where each file being transcribed, or waiting to be, has got to, by record ID.
    @Published private(set) var progress: [String: FileTranscription.Progress] = [:]
    /// Files dropped on History, picked, or opened with the app, waiting in the import sheet for the user to start them.
    @Published var offered: [URL] = []

    /// The types the file picker offers and drops are accepted as: anything with sound or pictures; one without an
    /// audio track AVFoundation can read is turned down with the reason.
    static let contentTypes: [UTType] = [.audiovisualContent]

    private unowned let controller: SessionController
    private let history = HistoryStore.shared
    private let settings = AppSettings.shared
    private var queue: [Job] = []
    private var running: Job?
    private var task: Task<Void, Never>?
    /// The running file's pipeline, so quitting can save the segments it already has.
    private var pipeline: TranscriptionPipeline?
    /// Set while a dictation is under way: the file then hands out no new audio (see `FileTranscription.outstandingLimit`).
    private let dictating = Flag()
    private var stateObservation: AnyCancellable?
    private let log = Logger(subsystem: "local.opentypeless.app", category: "files")

    private struct Job {
        let recordID: String
        /// The file to read; nil to transcribe the record's saved recording again (a retry).
        let file: URL?
        let cleanUp: Bool
        /// The record as it was before a retry, put back if the retry is cancelled before it starts.
        let original: DictationRecord?
    }

    /// Read from the thread that hands the file's audio over, written on the main actor.
    private final class Flag: @unchecked Sendable {
        private let lock = NSLock()
        private var _value = false
        var value: Bool {
            get { lock.withLock { _value } }
            set { lock.withLock { _value = newValue } }
        }
    }

    init(controller: SessionController) {
        self.controller = controller
        stateObservation = controller.$state.sink { [dictating] state in dictating.value = state != .idle }
    }

    /// Whether this record is a file being transcribed or waiting to be.
    func isActive(_ id: String) -> Bool { progress[id] != nil }

    /// Offers the audio and video files among `urls` for transcribing: the import sheet shows them.
    func offer(_ urls: [URL]) {
        let media = urls.filter(Self.isMedia)
        guard !media.isEmpty else { return }
        offered += media.filter { !offered.contains($0) }
    }

    static func isMedia(_ url: URL) -> Bool {
        contentType(of: url)?.conforms(to: .audiovisualContent) ?? false
    }

    static func isVideo(_ url: URL) -> Bool {
        contentType(of: url)?.conforms(to: .movie) ?? false
    }

    private static func contentType(of url: URL) -> UTType? {
        guard url.isFileURL else { return nil }
        return (try? url.resourceValues(forKeys: [.contentTypeKey]))?.contentType ?? UTType(filenameExtension: url.pathExtension)
    }

    /// Transcribes `urls` one after another, each into a new History entry; `cleanUp` cleans each transcript up.
    func transcribe(_ urls: [URL], cleanUp: Bool) {
        offered = []
        for url in urls {
            var record = history.create()
            record.status = .processing
            record.source = .file
            record.sourceName = url.lastPathComponent
            history.update(record)
            queue.append(Job(recordID: record.id, file: url, cleanUp: cleanUp, original: nil))
            progress[record.id] = .queued
            log.debug("queued \(url.lastPathComponent, privacy: .public)")
        }
        startNext()
    }

    /// Transcribes a file's saved recording again; the segments already transcribed are reused, as for a dictation.
    func retry(_ saved: DictationRecord) {
        guard saved.isFile, saved.hasAudio, !isActive(saved.id) else { return }
        var record = saved
        record.status = .processing
        record.error = nil
        history.update(record)
        queue.append(Job(recordID: record.id, file: nil, cleanUp: settings.fileCleanUp, original: saved))
        progress[record.id] = .queued
        startNext()
    }

    /// Stops a file: one waiting is taken off the queue (a new one deleted, a retry put back as it was); the one running
    /// stops where it is (see `run`).
    func cancel(_ id: String) {
        if running?.recordID == id {
            task?.cancel()
            return
        }
        guard let index = queue.firstIndex(where: { $0.recordID == id }) else { return }
        let job = queue.remove(at: index)
        progress[id] = nil
        guard let record = history.records.first(where: { $0.id == id }) else { return }
        if let original = job.original { history.update(original) } else { history.delete(record) }
    }

    /// Quitting: the segments the running file has are saved, so a retry at the next launch doesn't send them again.
    /// The records stay "processing" and show as interrupted then, like a dictation.
    func stopForQuit() {
        guard let running, let pipeline, var record = history.records.first(where: { $0.id == running.recordID }) else { return }
        record.chunkTexts = pipeline.completedTranscripts()
        history.update(record)
    }

    // MARK: - Running

    private func startNext() {
        guard running == nil, !queue.isEmpty else { return }
        let job = queue.removeFirst()
        running = job
        task = Task { [weak self] in
            await self?.run(job)
            self?.ended(job)
        }
    }

    private func ended(_ job: Job) {
        progress[job.recordID] = nil
        running = nil
        task = nil
        pipeline = nil
        startNext()
    }

    /// Moves a file's progress on, unless it has finished meanwhile.
    private func report(_ id: String, _ value: FileTranscription.Progress) {
        if progress[id] != nil, progress[id] != value { progress[id] = value }
    }

    /// Writes the record, unless it was deleted meanwhile (`update` would bring it back).
    private func store(_ record: DictationRecord) {
        guard history.records.contains(where: { $0.id == record.id }) else { return }
        history.update(record)
    }

    private func run(_ job: Job) async {
        guard var record = history.records.first(where: { $0.id == job.recordID }) else { return }
        guard let endpoint = settings.sttEndpoint, settings.isConfigured(settings.sttProvider) else {
            record.status = .failed
            record.error = L("请先在设置里配置语音转文字服务商（\(settings.sttProvider.displayName)）",
                             "Set up the speech-to-text provider (\(settings.sttProvider.displayName)) in Settings first")
            store(record)
            return
        }
        let id = record.id

        // The file goes into the record's recording first.
        let total: Int
        if let file = job.file {
            report(id, .reading(nil))
            do {
                let seconds = try await AudioFileDecoder.duration(of: file)
                // Refused at once when the recording can't fit, rather than after filling the disk.
                if let free = Self.freeBytes(at: record.folder), free < FileTranscription.requiredBytes(seconds: seconds) {
                    throw FileTranscriptionError.diskFull
                }
                let decoded = try await Self.read(file, to: record.audioURL, partial: record.partialAudioURL, seconds: seconds) { [weak self] fraction in
                    Task { @MainActor in self?.report(id, .reading(fraction)) }
                }
                record.duration = AudioFormat.seconds(forSampleCount: decoded.samples)
                total = decoded.chunks
            } catch {
                if error is CancellationError || Task.isCancelled {
                    // Nothing came of it yet.
                    log.debug("cancelled while reading \(record.sourceName ?? "", privacy: .public)")
                    if history.records.contains(where: { $0.id == id }) { history.delete(record) }
                    return
                }
                record.status = .failed
                record.error = FileTranscriptionError.reading(error).localizedDescription
                log.debug("can't read \(record.sourceName ?? "", privacy: .public): \(String(describing: error), privacy: .public)")
                store(record)
                return
            }
        } else {
            do {
                total = try await Self.countChunks(in: record.audioURL)
            } catch {
                if Task.isCancelled {
                    if let original = job.original { store(original) }
                    return
                }
                record.status = .failed
                record.error = L("找不到这条记录的录音文件", "The recording for this entry is gone")
                store(record)
                return
            }
        }

        // Saved transcripts are reused by chunk index, so only when the recording is cut into the same chunks again.
        let preset = record.chunkLayout == Chunker.layout ? record.chunkTexts : [:]
        record.chunkTexts = preset
        record.chunkLayout = Chunker.layout
        store(record)
        let pipeline = controller.makePipeline(endpoint: endpoint, preset: preset, forFile: true)
        self.pipeline = pipeline
        let started = ProcessInfo.processInfo.systemUptime
        report(id, .transcribing(done: min(pipeline.chunkCounts().answered, total), total: total))
        // Moves the progress on as chunks come back, and saves the transcripts so far now and then.
        let watch = Task { @MainActor [weak self] in
            var saved = ProcessInfo.processInfo.systemUptime
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 250_000_000)
                guard let self, !Task.isCancelled else { return }
                self.report(id, .transcribing(done: min(pipeline.chunkCounts().answered, total), total: total))
                if ProcessInfo.processInfo.systemUptime - saved >= FileTranscription.saveInterval,
                   var current = self.history.records.first(where: { $0.id == id }), current.status == .processing {
                    saved = ProcessInfo.processInfo.systemUptime
                    current.chunkTexts = pipeline.completedTranscripts()
                    self.history.update(current)
                }
            }
        }
        defer { watch.cancel() }

        let raw: String
        do {
            raw = try await withTaskCancellationHandler {
                try await Self.feed(record.audioURL, to: pipeline, dictating: dictating)
            } onCancel: {
                pipeline.cancel()
            }
        } catch {
            watch.cancel()
            record.chunkTexts = pipeline.completedTranscripts()
            record.status = .failed
            if let failure = error as? PipelineFailure {
                record.rawText = failure.partialText
                record.error = failure.localizedDescription
            } else {
                record.rawText = TranscriptJoiner.join(record.chunkTexts.sorted { $0.key < $1.key }.map(\.value))
                record.error = Task.isCancelled || APIError.from(error) == .cancelled
                    ? L("已取消（音频已保存，可重新转写）", "Cancelled (audio saved — you can re-transcribe it)")
                    : error.localizedDescription
            }
            log.debug("transcription stopped: \(record.error ?? "", privacy: .public)")
            controller.account(&record, pipeline: pipeline, polished: nil, discarded: [])
            store(record)
            return
        }
        watch.cancel()
        record.timing = DictationRecord.Timing(transcription: ProcessInfo.processInfo.systemUptime - started)
        let backupChunks = pipeline.backupChunkCount()
        if backupChunks > 0 { record.timing?.transcriptionBackupChunks = backupChunks }
        record.chunkTexts = pipeline.completedTranscripts()
        record.rawText = raw
        // A new transcript supersedes the clean-up of an earlier run.
        record.polishedText = nil
        record.error = nil

        var polished: SegmentedPolishResult?
        if raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            record.status = .done
            record.error = L("没有识别到语音", "No speech detected")
        } else if job.cleanUp, settings.polishEnabled {
            report(id, .cleaningUp)
            // Saved while the clean-up runs, so the transcript survives a crash.
            store(record)
            do {
                let outcome = try await controller.polish(raw, forFile: true, holdWhile: { [dictating] in dictating.value })
                polished = outcome
                record.timing?.polishFirstToken = outcome.firstTokenSeconds
                record.timing?.polish = outcome.totalSeconds
                record.timing?.polishModel = outcome.model
                record.timing?.usedBackup = outcome.usedBackup
                let fallbacks = outcome.fallbacks
                if fallbacks.count == outcome.parts.count {
                    record.error = fallbacks.lazy.compactMap(\.error).first?.localizedDescription
                    record.status = .polishFailed
                } else {
                    record.polishedText = outcome.text
                    record.status = .done
                }
            } catch {
                // Cancelled while cleaning up: the transcript is all there, so it stays, as it was transcribed.
                record.error = APIError.from(error) == .cancelled ? L("已取消", "Cancelled") : error.localizedDescription
                record.status = .polishFailed
            }
        } else {
            record.status = .done
        }
        log.debug("transcribed \(record.sourceName ?? "", privacy: .public): \(String(format: "%.0f", record.duration))s, \(total) segments, \(record.status.rawValue, privacy: .public)")
        controller.account(&record, pipeline: pipeline, polished: polished, discarded: [])
        store(record)
    }

    // MARK: - Off the main actor

    /// Reads `file` into a recording at `destination`, written under `partial` and renamed once all of it is there, so a
    /// recording only ever exists complete. Returns how many samples it holds and how many chunks the pipeline will cut
    /// it into (the chunker cuts in the same places however the audio is split into blocks).
    nonisolated private static func read(_ file: URL, to destination: URL, partial: URL, seconds: Double,
                                         onProgress: @escaping @Sendable (Double) -> Void) async throws -> (samples: Int, chunks: Int) {
        try? FileManager.default.removeItem(at: partial)
        let writer: WAVFileWriter
        do {
            writer = try WAVFileWriter(url: partial)
        } catch {
            throw FileTranscriptionError.writing(error)
        }
        var closed = false
        let chunker = Chunker()
        var chunks = 0
        var count = 0
        var reported = 0.0
        let expected = AudioFormat.sampleCount(forSeconds: seconds)
        do {
            try await AudioFileDecoder.decode(file) { samples in
                writer.append(samples)
                if let failure = writer.failure { throw FileTranscriptionError.writing(failure) }
                chunks += chunker.append(samples).count
                count += samples.count
                if expected > 0 {
                    let fraction = min(1, Double(count) / Double(expected))
                    if fraction - reported >= 0.01 {
                        reported = fraction
                        onProgress(fraction)
                    }
                }
            }
            writer.close()
            closed = true
            if let failure = writer.failure { throw FileTranscriptionError.writing(failure) }
            guard count > 0 else { throw FileTranscriptionError.noSound }
            if chunker.finish() != nil { chunks += 1 }
            try? FileManager.default.removeItem(at: destination)
            do {
                try FileManager.default.moveItem(at: partial, to: destination)
            } catch {
                throw FileTranscriptionError.writing(error)
            }
        } catch {
            if !closed { writer.close() }
            try? FileManager.default.removeItem(at: partial)
            throw error
        }
        return (count, chunks)
    }

    /// How many chunks a saved recording is cut into, for the progress of a retry.
    nonisolated private static func countChunks(in audio: URL) async throws -> Int {
        let reader = try WAVFileReader(url: audio)
        let chunker = Chunker()
        var chunks = 0
        while let samples = try reader.read(maxSamples: AudioFormat.sampleCount(forSeconds: FileTranscription.blockSeconds)) {
            try Task.checkCancellation()
            await Task.yield()
            chunks += chunker.append(samples).count
        }
        return chunks + (chunker.finish() == nil ? 0 : 1)
    }

    /// Hands the recording to the pipeline from disk (see `FileTranscription.feed`), then waits for the transcript.
    nonisolated private static func feed(_ audio: URL, to pipeline: TranscriptionPipeline, dictating: Flag) async throws -> String {
        let reader = try WAVFileReader(url: audio)
        return try await FileTranscription.feed(reader, to: pipeline, dictating: { dictating.value })
    }

    /// Space left on the volume holding `folder` for files the user is saving; nil when the system doesn't say.
    nonisolated private static func freeBytes(at folder: URL) -> Int64? {
        let values = try? folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }
}
