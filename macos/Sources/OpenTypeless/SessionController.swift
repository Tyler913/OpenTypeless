import AppKit
import os
import TypelessCore

/// Thread-safe running sample count (written on the audio thread, read on main).
private final class SampleCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    func add(_ n: Int) { lock.withLock { count += n } }
    var seconds: Double { lock.withLock { AudioFormat.seconds(forSampleCount: count) } }
}

/// Orchestrates one dictation: hotkey → record → chunked STT → polish → paste.
@MainActor
final class SessionController: ObservableObject {
    enum State: Equatable { case idle, recording, processing }

    @Published private(set) var state: State = .idle
    @Published private(set) var lastError: String?

    let hud = HUDController()
    private let editWatcher = EditWatcher()
    private let settings = AppSettings.shared
    private let history = HistoryStore.shared
    private let recorder = AudioRecorder()
    /// Receives every microphone sample; hands them to the running dictation, or keeps a pre-roll in between.
    private let sink = AudioSink()
    /// The input the running recorder was opened on (it may keep running between dictations when warm).
    private var openDeviceUID: String?
    /// Audio from before the key was pressed at the start of this dictation (only with a warm microphone).
    private var preRollSeconds: Double = 0
    /// On-device recognition shown above the capsule while recording, when Live preview is on.
    private var preview: LivePreview?

    private var pipeline: TranscriptionPipeline?
    private var writer: WAVFileWriter?
    private var record: DictationRecord?
    private var counter = SampleCounter()
    /// When the hotkey went down, by the key event's own time (`ProcessInfo.systemUptime` clock, see `HotkeyPress`).
    private var pressedAt: TimeInterval?
    private var handsFree = false
    /// Which dictation is current: bumped whenever one ends or is cancelled, so work still out for an earlier one (its
    /// paste, its Return, its reset) can tell it's no longer wanted and leaves the next dictation alone.
    private var generation = 0
    /// The app in front when the recording stopped, where the text is meant to go (nil for a retry).
    private var pasteTarget: pid_t?
    /// Between the screen locking (or sleep) and unlocking: no warm microphone meanwhile.
    private var isLocked = false
    /// Cancelled recordings still being transcribed in the background (see `keepCancelledRecording`). A retry from
    /// History takes the record over, and the background result is then dropped rather than written over the retry's.
    private var keptInBackground: Set<String> = []
    private var processingTask: Task<Void, Never>?
    private var maxDurationTimer: Timer?
    /// Keeps the connections warm while recording (see `warmConnections`).
    private var warmTimer: Timer?
    private let preconnector = Preconnector()
    /// Silences the speakers while recording, when that's switched on.
    private let outputMute = OutputMute()
    /// A clean-up started before the key was released, on the transcript a speculative tail gave.
    private var speculativePolish: SpeculativePolish?
    /// Clean-ups started ahead of time that finished but were then dropped because the speaker went on: they were billed too.
    private var discardedPolishes: [SegmentedPolishResult] = []
    private var modelInfo: [String: APIClient.ModelInfo] = [:]
    /// False when retrying from the history window, or once the screen locked: the result goes to the clipboard instead.
    private var deliverByPaste = true

    init() {
        editWatcher.onCorrections = { [weak self] corrections, quiet in
            guard let self, self.settings.learnFromEdits else { return }
            let added = self.settings.learn(corrections)
            self.note("learned from edits: \(corrections.map { "\($0.heard) → \($0.corrected)" }), new: \(added)")
            if !added.isEmpty, !quiet, self.state == .idle {
                self.hud.show(.learned(added.joined(separator: L("、", ", "))), autoHideAfter: 2.5)
            }
        }
        recorder.onFailure = { [weak self] error in
            Task { @MainActor in
                self?.abort(message: L("录音中断：", "Recording interrupted: ") + error.localizedDescription)
            }
        }
        // Set once: while the microphone is kept warm the recorder runs between dictations too.
        recorder.onSamples = { [sink] samples in sink.deliver(samples) }
        recorder.onLevel = { [sink, hudModel = hud.model] level in
            guard sink.isRecording else { return }
            DispatchQueue.main.async { hudModel.push(level: level) }
        }
        // A warm recorder that fell back to the default input when the chosen one went away goes back to it once it's
        // plugged in again (the engine only follows the default on its own).
        Microphones.observeDeviceList { [weak self] in
            MainActor.assumeIsolated { self?.updateWarmMicrophone() }
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil,
                                                          queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemLocked("going to sleep") }
        }
        DistributedNotificationCenter.default().addObserver(forName: .init("com.apple.screenIsLocked"), object: nil,
                                                            queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemLocked("screen locked") }
        }
        DistributedNotificationCenter.default().addObserver(forName: .init("com.apple.screenIsUnlocked"), object: nil,
                                                            queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemUnlocked() }
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil,
                                                          queue: .main) { [weak self] _ in
            // Waking to the login screen is unlocked later, by the notification above.
            MainActor.assumeIsolated { if !SessionController.screenIsLocked { self?.systemUnlocked() } }
        }
    }

    private var chosenDeviceUID: String? { settings.microphoneUID.isEmpty ? nil : settings.microphoneUID }

    /// The running dictation is still capturing: recording, or in the moment after the key is released while the last
    /// of the audio comes in (see `finishRecording`).
    private var isCapturing: Bool { writer != nil }

    /// With "Keep microphone ready" on, keeps the recorder running between dictations (on the chosen input), so a
    /// dictation starts at once and includes the moment before the key; otherwise lets it stop. Called at launch,
    /// when settings change, when a device is connected or disconnected, and after each dictation.
    func updateWarmMicrophone() {
        guard state == .idle else { return }
        if settings.keepMicrophoneWarm, Permissions.microphoneGranted, !isLocked {
            // Running on the right input, or on the default because the chosen one is still disconnected.
            if recorder.isRecording, openDeviceUID == chosenDeviceUID,
               recorder.isOnChosenDevice || chosenDeviceUID.flatMap(Microphones.deviceID(uid:)) == nil { return }
            recorder.stop()
            recorder.deviceUID = chosenDeviceUID
            do {
                try recorder.start()
                openDeviceUID = chosenDeviceUID
                note("microphone kept warm")
            } catch {
                note("couldn't keep the microphone warm: \(error.localizedDescription)")
            }
        } else if recorder.isRecording {
            recorder.stop()
            openDeviceUID = nil
            // Nor is what it heard last kept for the next dictation (from before the screen locked, say).
            sink.end()
        }
    }

    // MARK: - Hotkey events

    private let log = Logger(subsystem: "local.opentypeless.app", category: "session")
    private func note(_ text: String) { log.debug("\(text, privacy: .public)") }

    /// `time`: when the key went down (the event's own time, see `HotkeyPress`).
    func hotkeyPressed(at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        switch state {
        case .idle: note("start recording")
        case .recording where handsFree: note("stop (hands-free)")
        case .recording: note("press ignored — already recording")
        case .processing: note("press ignored — still processing the previous dictation")
        }
        switch state {
        case .idle:
            startRecording(pressedAt: time)
        case .recording where handsFree:
            finishRecording()
        default:
            break
        }
    }

    func hotkeyReleased(at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        guard state == .recording, !handsFree, let pressedAt else { return }
        let held = time - pressedAt
        let tap = HotkeyPress.isTap(pressedAt: pressedAt, releasedAt: time)
        note(tap ? String(format: "tap (%.2fs) → hands-free", held) : String(format: "released after %.2fs → stop", held))
        if tap {
            handsFree = true
        } else {
            finishRecording()
        }
    }

    /// Fn+F-key and similar combos shouldn't start a dictation.
    func otherKeyPressed(at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        guard state == .recording, !handsFree, let pressedAt,
              HotkeyPress.isCombo(pressedAt: pressedAt, otherKeyAt: time) else { return }
        note("cancelled — another key was pressed with the shortcut (treated as a key combo)")
        cancel(silently: true)
    }

    /// The Mac is going to sleep or the screen was locked. A hands-free recording mustn't keep the microphone open on
    /// the lock screen, nor its text land in whatever has focus afterwards: a recording stops as with Esc (a long one is
    /// kept in History), and one being processed goes to the clipboard instead of being pasted.
    func systemLocked(_ reason: String) {
        // A warm microphone doesn't stay on behind the lock screen either; see `systemUnlocked`.
        isLocked = true
        defer { updateWarmMicrophone() }
        switch state {
        case .idle:
            return
        case .recording:
            note("\(reason) while recording — cancelled as with Esc")
            escapePressed()
        case .processing:
            note("\(reason) while processing — the text goes to the clipboard")
            deliverByPaste = false
        }
    }

    /// The screen was unlocked (or the Mac woke without locking it): a warm microphone can come back on.
    func systemUnlocked() {
        guard isLocked else { return }
        isLocked = false
        updateWarmMicrophone()
    }

    /// Whether the login screen is up, from the window server's session.
    static var screenIsLocked: Bool {
        (CGSessionCopyCurrentDictionary() as? [String: Any])?["CGSSessionScreenIsLocked"] as? Bool ?? false
    }

    func escapePressed() {
        guard state != .idle else { return }
        if state == .recording, CancelPolicy.keeps(recordedSeconds: counter.seconds - preRollSeconds) {
            keepCancelledRecording()
        } else {
            cancel(silently: false)
        }
    }

    /// Esc on a long recording: stop and insert nothing, but keep it. The rest is transcribed in the background
    /// (no clean-up), so the text is in History for a day, where it can also be re-transcribed.
    private func keepCancelledRecording() {
        guard state == .recording, var record, let pipeline else { return }
        stopCapture()
        playSound("Pop")
        record.duration = counter.seconds
        record.status = .cancelled
        history.update(record)
        note(String(format: "cancelled after %.1fs — kept in History", record.duration))
        let discarded = takeDiscardedPolishes()
        reset()
        hud.show(.error(L("已取消，录音在历史记录中保留 24 小时", "Cancelled — kept in History for 24 hours")), autoHideAfter: 2.5)

        let kept = record
        keptInBackground.insert(kept.id)
        Task { [weak self] in
            var record = kept
            var raw: String
            do {
                raw = try await pipeline.finish()
            } catch let failure as PipelineFailure {
                raw = failure.partialText
            } catch {
                raw = TranscriptJoiner.join(pipeline.completedTranscripts().sorted { $0.key < $1.key }.map(\.value))
            }
            guard let self else { return }
            if self.keptInBackground.remove(record.id) == nil {
                // Retried from History meanwhile: that run owns the record now. Only what this one spent is counted.
                self.account(&record, pipeline: pipeline, polished: nil, discarded: discarded)
                return
            }
            guard self.history.records.contains(where: { $0.id == record.id }) else { return }
            record.rawText = raw
            record.chunkTexts = pipeline.completedTranscripts()
            self.account(&record, pipeline: pipeline, polished: nil, discarded: discarded)
            self.history.update(record)
        }
    }

    // MARK: - Recording

    /// `pressedAt` is the key event's own time: opening a cold microphone (a Bluetooth one can take a second or more)
    /// mustn't count against how long the key was held.
    private func startRecording(pressedAt: TimeInterval) {
        // First of all, so the connections are ready by the time the first request goes out.
        warmConnections()
        // Someone is dictating, so the screen is unlocked, whatever notification was missed (a wake without a lock).
        isLocked = false
        guard let sttEndpoint = settings.sttEndpoint, settings.isConfigured(settings.sttProvider) else {
            note("not started — speech-to-text provider not configured")
            hud.show(.error(L("请先在设置里配置语音转文字服务商（\(settings.sttProvider.displayName)）",
                              "Set up the speech-to-text provider (\(settings.sttProvider.displayName)) in Settings first")),
                     autoHideAfter: 3)
            NotificationCenter.default.post(name: .openSettings, object: nil, userInfo: ["page": "providers"])
            return
        }
        guard Permissions.microphoneGranted else {
            note("not started — no microphone permission")
            Permissions.requestMicrophone { [weak self] granted in
                if !granted {
                    self?.hud.show(.error(L("需要麦克风权限：系统设置 → 隐私与安全性 → 麦克风",
                                            "Microphone access needed: System Settings → Privacy & Security → Microphone")),
                                   autoHideAfter: 4)
                }
            }
            return
        }

        // Whatever the user did to the previous dictation is final now.
        editWatcher.finish(quiet: true, reason: "next dictation")
        if settings.learnFromEdits { editWatcher.prepare() }

        var record = history.create()
        record.status = .recording
        history.update(record)

        let writer: WAVFileWriter
        do {
            writer = try WAVFileWriter(url: record.audioURL)
        } catch {
            // Otherwise the record stays "recording" and turns into a failed entry with no audio at the next launch.
            history.delete(record)
            hud.show(.error(L("无法创建录音文件：", "Can't create the recording file: ") + error.localizedDescription),
                     autoHideAfter: 4)
            return
        }

        let recordID = record.id
        let pipeline = makePipeline(endpoint: sttEndpoint, preset: [:], onSpeculation: { [weak self] raw in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self, self.record?.id == recordID else { return }
                    self.speculationChanged(raw)
                }
            }
        })
        discardedPolishes = []
        let counter = SampleCounter()
        // A warm recorder on another input (the choice changed) is reopened on the right one.
        if recorder.isRecording, openDeviceUID != chosenDeviceUID { recorder.stop() }
        var livePreview: LivePreview?
        if settings.livePreview {
            let hudModel = hud.model
            livePreview = LivePreview { text in
                let line = LivePreviewText.tail(text)
                DispatchQueue.main.async { hudModel.preview = line }
            }
            livePreview?.start(language: settings.sttLanguage)
        }
        preview = livePreview
        hud.model.preview = ""
        // Starts with the pre-roll when the microphone was already running.
        let preRoll = sink.begin { [livePreview] samples in
            writer.append(samples)
            pipeline.append(samples)
            counter.add(samples.count)
            livePreview?.append(samples)
        }
        preRollSeconds = AudioFormat.seconds(forSampleCount: preRoll)
        if !recorder.isRecording {
            recorder.deviceUID = chosenDeviceUID
            do {
                try recorder.start()
                openDeviceUID = chosenDeviceUID
            } catch {
                sink.end()
                preview?.stop()
                preview = nil
                writer.close()
                history.delete(record)
                hud.show(.error(L("无法开始录音：", "Can't start recording: ") + error.localizedDescription), autoHideAfter: 4)
                return
            }
        }

        self.record = record
        self.writer = writer
        self.pipeline = pipeline
        self.counter = counter
        deliverByPaste = true
        self.pressedAt = pressedAt
        handsFree = false
        state = .recording
        lastError = nil
        hud.model.resetLevels()
        hud.model.startedAt = Date()
        hud.show(.recording)
        playSound("Tink")
        // After the start sound, which muting would cut off.
        if settings.muteWhileRecording { outputMute.mute(after: .milliseconds(settings.playSounds ? 300 : 0)) }

        maxDurationTimer = Timer.scheduledTimer(
            withTimeInterval: TimeInterval(max(1, settings.maxRecordingMinutes) * 60), repeats: false
        ) { [weak self] _ in
            Task { @MainActor in self?.finishRecording() }
        }
        // A long dictation keeps the clean-up's connection (idle while speech-to-text works) from timing out.
        warmTimer = Timer.scheduledTimer(withTimeInterval: 25, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.warmConnections() }
        }
    }

    /// Opens the connections this dictation's requests will use (speech-to-text and clean-up, main and backup routes)
    /// ahead of them, so the first request after the key is released doesn't wait for DNS, TCP and TLS. Each host once.
    private func warmConnections() {
        var endpoints: [ProviderEndpoint?] = [settings.sttEndpoint, settings.sttBackupEndpoint]
        if settings.polishEnabled { endpoints += [settings.polishEndpoint, settings.polishBackupEndpoint] }
        preconnector.warm(endpoints.map { $0.map { APIClient(endpoint: $0) } })
    }

    /// How long each speech-to-text route takes, kept while the app runs, so the backup is asked when a chunk is late.
    private var latencies: [String: TranscriptionLatency] = [:]

    private func makePipeline(endpoint: ProviderEndpoint, preset: [Int: String],
                              onSpeculation: (@Sendable (String?) -> Void)? = nil) -> TranscriptionPipeline {
        let language = settings.sttLanguage.isEmpty ? nil : settings.sttLanguage
        let backup = settings.sttBackupEndpoint.map {
            (client: APIClient(endpoint: $0),
             options: APIClient.TranscriptionOptions(model: settings.sttBackupModel.trimmingCharacters(in: .whitespaces), language: language,
                                                     vocabulary: settings.vocabularyList))
        }
        let key = ModelPrice.key(settings.sttProvider, settings.sttModel)
        let latency = latencies[key] ?? TranscriptionLatency()
        latencies[key] = latency
        return TranscriptionPipeline(
            client: APIClient(endpoint: endpoint),
            options: .init(model: settings.sttModel, language: language, vocabulary: settings.vocabularyList),
            preset: preset,
            backup: backup,
            latency: latency,
            onSpeculation: onSpeculation
        )
    }

    private func stopCapture() {
        maxDurationTimer?.invalidate()
        maxDurationTimer = nil
        warmTimer?.invalidate()
        warmTimer = nil
        outputMute.restore()
        // Stopping delivers what the recorder still holds, so a cold recorder stops first. After `end` nothing
        // reaches the writer; a warm recorder goes back to filling the pre-roll.
        if !settings.keepMicrophoneWarm {
            recorder.stop()
            openDeviceUID = nil
        }
        sink.end()
        preview?.stop()
        preview = nil
        hud.model.preview = ""
        writer?.close()
        // The dictation went on from the audio in memory; only a retry from History would miss what didn't reach the file.
        if let failure = writer?.failure { note("recording file incomplete: \(failure.localizedDescription)") }
        writer = nil
    }

    /// How long capture goes on after the key is released, in nanoseconds. The microphone hands its audio over a block
    /// at a time: the block with the last syllable would otherwise be cut off when the tap is removed or, with a warm
    /// microphone, land in the pre-roll and be thrown away.
    private static let releaseTail: UInt64 = 100_000_000

    private func finishRecording() {
        guard state == .recording, var record, let pipeline else { return }
        record.duration = counter.seconds
        if record.duration - preRollSeconds < 0.4 {
            stopCapture()
            playSound("Pop")
            note(String(format: "discarded — only %.2fs of audio", record.duration))
            // Accidental tap.
            pipeline.cancel()
            history.delete(record)
            reset()
            hud.show(.hidden)
            return
        }

        record.status = .processing
        history.update(record)
        self.record = record
        state = .processing
        // Where the text should go: if another app is in front by the time it's ready, it goes to the clipboard.
        pasteTarget = NSWorkspace.shared.frontmostApplication?.processIdentifier
        hud.showWorking(polishes: settings.polishEnabled)

        // No waiting when the speaker paused before letting go and that pause is already being transcribed as the last
        // chunk: the moment's (quiet) audio changes nothing then, and the text may be ready at once.
        let waitsForTail = !pipeline.endsInCoveredPause
        let released = record
        let generation = self.generation
        processingTask = Task { [weak self] in
            if waitsForTail { try? await Task.sleep(nanoseconds: SessionController.releaseTail) }
            // Cancelled meanwhile: `cancel` stopped the capture itself.
            guard let self, !Task.isCancelled else { return }
            // Then the stop sound, which the microphone would have picked up (and muting would have cut off).
            self.stopCapture()
            self.playSound("Pop")
            var record = released
            record.duration = self.counter.seconds
            self.record = record
            await self.process(record: record, pipeline: pipeline, generation: generation)
        }
    }

    // MARK: - Processing

    private func process(record initial: DictationRecord, pipeline: TranscriptionPipeline, generation: Int) async {
        var record = initial
        let started = ProcessInfo.processInfo.systemUptime
        // Moves the bar as the speech-to-text chunks still out come back.
        let bar = hud.model.bar
        let watch = Task { @MainActor in
            while !Task.isCancelled {
                if let counts = pipeline.transcriptionProgress() {
                    bar.reach(.transcribing(done: counts.done, total: counts.total))
                }
                try? await Task.sleep(nanoseconds: 50_000_000)
            }
        }
        defer { watch.cancel() }
        do {
            let raw = try await pipeline.finish()
            watch.cancel()
            // Cancelled (and maybe a new dictation started) while the text came back: `cancel` has dealt with this one.
            guard isCurrent(generation) else { return }
            bar.reach(.transcribed)
            record.timing = DictationRecord.Timing(transcription: ProcessInfo.processInfo.systemUptime - started)
            let backupChunks = pipeline.backupChunkCount()
            if backupChunks > 0 { record.timing?.transcriptionBackupChunks = backupChunks }
            record.timing?.speculativeTail = pipeline.tailWasSpeculative
            record.timing?.lastChunkRoute = pipeline.lastChunkUsedBackup.map { $0 ? "backup" : "primary" }
            if pipeline.tailWasSpeculative {
                note(String(format: "last segment transcribed ahead of time: text %.2fs after release",
                            ProcessInfo.processInfo.systemUptime - started))
            }
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = raw
            record.error = nil
            guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                record.status = .done
                account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes())
                history.update(record)
                finish(showing: .error(L("没有识别到语音", "No speech detected")), hideAfter: 2)
                return
            }
            await polishAndDeliver(record: &record, pipeline: pipeline, generation: generation)
        } catch let failure as PipelineFailure {
            guard isCurrent(generation) else { return }
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = failure.partialText
            record.status = .failed
            record.error = failure.localizedDescription
            account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes())
            history.update(record)
            lastError = failure.localizedDescription
            finish(showing: .error(L("转写失败，可在菜单栏重试", "Failed — retry from the menu bar")), hideAfter: 4)
        } catch {
            if APIError.from(error) == .cancelled || !isCurrent(generation) { return }
            record.status = .failed
            record.error = error.localizedDescription
            account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes())
            history.update(record)
            lastError = error.localizedDescription
            finish(showing: .error(error.localizedDescription), hideAfter: 5)
        }
    }

    private func polishAndDeliver(record: inout DictationRecord, pipeline: TranscriptionPipeline, generation: Int) async {
        let begun = ProcessInfo.processInfo.systemUptime
        var text = record.rawText
        var notice: String?
        var polished: SegmentedPolishResult?

        if settings.polishEnabled {
            // When the speaker paused before letting go and said nothing more, the clean-up of this very transcript
            // started while they were still holding the key: take it over (often it's already done).
            let polishing: Task<SegmentedPolishResult, Error>
            var ahead = 0.0
            // One that already failed (while recording) gets a fresh attempt instead.
            if let speculative = speculativePolish, speculative.raw == record.rawText, !speculative.outcome.failed {
                speculativePolish = nil
                polishing = speculative.task
                // What it streams from now on moves this dictation's bar.
                speculative.partials.target = onPolishPartial(expected: record.rawText)
                ahead = ProcessInfo.processInfo.systemUptime - speculative.startedAt
                let transcription = record.timing?.transcription ?? 0
                record.timing?.polishHeadStart = max(0, ahead - transcription)
                note(String(format: "clean-up started %.2fs ago, while recording", ahead))
            } else {
                dropSpeculativePolish()
                let raw = record.rawText
                let onPartial = onPolishPartial(expected: raw)
                polishing = Task { try await self.polish(raw, onPartial: onPartial) }
            }
            // Saved while the clean-up runs, so the transcript survives a crash.
            history.update(record)
            do {
                let outcome = try await withTaskCancellationHandler {
                    try await polishing.value
                } onCancel: {
                    polishing.cancel()
                }
                polished = outcome
                // The wait after the key was released: a clean-up started ahead of time had a head start.
                let firstToken = max(0, outcome.firstTokenSeconds - ahead)
                let total = max(0, outcome.totalSeconds - ahead)
                record.timing?.polishFirstToken = firstToken
                record.timing?.polish = total
                record.timing?.polishModel = outcome.model
                record.timing?.usedBackup = outcome.usedBackup
                let fallbacks = outcome.fallbacks
                note(String(format: "polish: %@%@, %d part(s), first token %.2fs, total %.2fs", outcome.model,
                            outcome.usedBackup ? " (backup)" : "", outcome.parts.count, firstToken, total)
                     + (fallbacks.isEmpty ? "" : "; inserted as spoken: "
                        + fallbacks.map { $0.fallback?.rawValue ?? "" }.joined(separator: ", ")))
                if fallbacks.count == outcome.parts.count {
                    // Cut off, an answer, a summary or a translation rather than the speaker's text, part after part.
                    notice = L("未整理，已插入原文", "Inserted without clean-up")
                    record.error = fallbacks.lazy.compactMap(\.error).first?.localizedDescription
                    record.status = .polishFailed
                } else {
                    // A part whose clean-up was unusable goes in as spoken, between the parts that were cleaned up.
                    if !fallbacks.isEmpty { notice = L("部分未整理，已插入原文", "Partly inserted without clean-up") }
                    text = outcome.text
                    record.polishedText = outcome.text
                    record.status = .done
                }
            } catch {
                if APIError.from(error) == .cancelled { return }
                notice = L("整理失败，已插入原文", "Clean-up failed — inserted raw text")
                record.error = error.localizedDescription
                record.status = .polishFailed
            }
        } else {
            record.status = .done
        }
        if record.status == .polishFailed { record.polishedText = nil }

        // Taken now: once the text is in, the next dictation may already be collecting its own.
        let discarded = takeDiscardedPolishes()
        if isCurrent(generation) {
            hud.model.bar.reach(.delivered)
            let transcription = record.timing?.transcription ?? 0
            record.timing?.delivered = transcription + ProcessInfo.processInfo.systemUptime - begun
            record.timing?.delivery = await deliver(text, notice: notice, generation: generation)
        }
        // Esc came before the text was in place (or while it went in): `cancel` has kept the record as cancelled, and
        // so it stays, now with its clean-up.
        if Task.isCancelled { record.status = .cancelled }
        // Costs, the usage ledger and History are written once the text is in place: nobody is waiting on them.
        account(&record, pipeline: pipeline, polished: polished, discarded: discarded)
        history.update(record)
    }

    /// Whether the dictation `generation` is still the one under way, not cancelled or followed by another.
    private func isCurrent(_ generation: Int) -> Bool {
        generation == self.generation && !Task.isCancelled
    }

    /// Whether the app in front is no longer the one the recording was made for.
    private var focusMoved: Bool {
        guard let pasteTarget else { return false }
        return NSWorkspace.shared.frontmostApplication?.processIdentifier != pasteTarget
    }

    // MARK: - Clean-up ahead of time

    /// A clean-up of `raw`, started while recording.
    private struct SpeculativePolish {
        let raw: String
        let startedAt: TimeInterval
        let task: Task<SegmentedPolishResult, Error>
        let outcome: PolishOutcome
        let partials: PartialRelay
    }

    /// Where a clean-up started ahead of time sends its partial text: nowhere until the dictation takes it over, so one
    /// that is dropped never moves the bar.
    private final class PartialRelay: @unchecked Sendable {
        private let lock = NSLock()
        private var _target: (@Sendable (String) -> Void)?
        var target: (@Sendable (String) -> Void)? {
            get { lock.withLock { _target } }
            set { lock.withLock { _target = newValue } }
        }
        func send(_ text: String) { target?(text) }
    }

    /// The answer of a clean-up started ahead of time, once it's in (a `Task`'s value can't be read without waiting).
    @MainActor private final class PolishOutcome {
        var result: SegmentedPolishResult?
        var failed = false
    }

    /// The pipeline has the whole transcript the recording would give if it ended now (the speaker paused), or no longer
    /// has one (they went on). Starts cleaning that transcript up, so the text can be ready when the key is released.
    private func speculationChanged(_ raw: String?) {
        guard state == .recording, settings.polishEnabled else { return }
        if let raw, raw == speculativePolish?.raw { return }
        dropSpeculativePolish()
        guard let raw, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        // Only a transcript short enough for one request: a long one would go out as several at every pause, and its
        // parts are cleaned up in parallel once the key is released anyway.
        guard !SegmentedPolish.needsParts(raw) else { return }
        let outcome = PolishOutcome()
        let partials = PartialRelay()
        let task = Task { [weak self] () async throws -> SegmentedPolishResult in
            guard let self else { throw APIError.cancelled }
            do {
                let result = try await self.polish(raw, onPartial: { partials.send($0) })
                outcome.result = result
                return result
            } catch {
                outcome.failed = true
                throw error
            }
        }
        speculativePolish = SpeculativePolish(raw: raw, startedAt: ProcessInfo.processInfo.systemUptime, task: task,
                                              outcome: outcome, partials: partials)
        note("clean-up started ahead of time (speaker paused)")
    }

    private func dropSpeculativePolish() {
        guard let speculative = speculativePolish else { return }
        speculativePolish = nil
        if let result = speculative.outcome.result { discardedPolishes.append(result) } else { speculative.task.cancel() }
    }

    /// The clean-ups done ahead of time that this dictation won't use, for its costs: the one still held is dropped
    /// first, so it isn't left for `reset` to drop after the costs were written (and then lost).
    private func takeDiscardedPolishes() -> [SegmentedPolishResult] {
        dropSpeculativePolish()
        defer { discardedPolishes = [] }
        return discardedPolishes
    }

    /// Moves the bar as the clean-up streams in: its first token, then its length against the transcript's.
    private func onPolishPartial(expected transcript: String) -> @Sendable (String) -> Void {
        let expected = transcript.utf16.count
        let bar = hud.model.bar
        return { text in
            let received = text.utf16.count
            DispatchQueue.main.async { bar.reach(.polishing(received: received, expected: expected)) }
        }
    }

    /// Pastes at the cursor unless focus is clearly not a text input, or has moved to another app since the recording
    /// stopped. Whether the paste actually landed is detected from the target app reading the clipboard: if it did, the
    /// user's previous clipboard is restored; if not, the text stays on the clipboard and the HUD says so. Returns how it
    /// went, for History (see `DictationRecord.Timing.delivery`).
    ///
    /// Esc (or the screen locking) can come at any point while the text goes in: the paste and the Return are only sent
    /// while the dictation is still current, and a cancelled one leaves the session and the HUD to what replaced it.
    @discardableResult
    private func deliver(_ text: String, notice: String?, generation: Int) async -> String {
        let target = deliverByPaste ? FocusProbe.focusedTarget() : .notEditable
        if target == .notEditable || deliverByPaste && focusMoved {
            note("deliver → clipboard (\(target == .notEditable ? "focus not editable" : "another app in front")) in \(frontmostID)")
            TextInserter.copyToClipboard(text)
            finish(showing: notice.map { .error($0) } ?? .copied, hideAfter: 1.6)
            return "copied"
        }
        let outcome = await TextInserter.insert(text, restoreClipboard: settings.restoreClipboard) {
            self.isCurrent(generation) && self.deliverByPaste && !self.focusMoved
        }
        guard isCurrent(generation) else {
            note("deliver → \(outcome), but the dictation was cancelled meanwhile")
            return "cancelled"
        }
        note("deliver → \(outcome) (focus \(target)) in \(frontmostID)")
        switch outcome {
        case .pasted:
            // Sends it, in a chat box or an AI prompt. Then there's no text left to watch for corrections.
            var sent = false
            if settings.pressEnterAfterInsert {
                // Not into another app that came to the front after the paste, nor once the screen locked.
                sent = await TextInserter.pressReturn {
                    self.isCurrent(generation) && self.deliverByPaste && !self.focusMoved
                }
                guard isCurrent(generation) else { return "pasted" }
            }
            if let notice {
                finish(showing: .error(notice), hideAfter: 3)
            } else {
                reset()
                hud.finishWorking()
            }
            if settings.learnFromEdits, !sent { editWatcher.watch(inserted: text) }
            return "pasted"
        case .notPasted:
            finish(showing: .copied, hideAfter: 1.6)
            return "failed"
        case .skipped:
            // The screen locked or another app came to the front just as ⌘V was due.
            TextInserter.copyToClipboard(text)
            finish(showing: notice.map { .error($0) } ?? .copied, hideAfter: 1.6)
            return "copied"
        case .noPermission:
            finish(showing: .error(L("没有辅助功能权限，文字已复制到剪贴板",
                                     "No Accessibility permission — text copied to the clipboard")), hideAfter: 3)
            return "failed"
        }
    }

    private var frontmostID: String { NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "?" }

    private func polish(_ raw: String, onPartial: (@Sendable (String) -> Void)? = nil) async throws -> SegmentedPolishResult {
        guard let endpoint = settings.polishEndpoint, settings.isConfigured(settings.polishProvider) else {
            throw APIError.missingAPIKey(settings.polishProvider.displayName)
        }
        let primary = route(endpoint, model: settings.polishModel)
        let backup = settings.polishBackupEndpoint.map { route($0, model: settings.polishBackupModel) }
        return try await SegmentedPolish.run(transcript: raw, primary: primary, backup: backup, onPartial: onPartial)
    }

    private func route(_ endpoint: ProviderEndpoint, model: String) -> PolishRoute {
        let model = model.trimmingCharacters(in: .whitespaces)
        return PolishRoute(client: APIClient(endpoint: endpoint), options: PolishOptions(
            model: model,
            vocabulary: settings.vocabularyList,
            misheard: settings.misheardHints,
            extraInstructions: settings.extraInstructions,
            modelInfo: endpoint.id == .openrouter ? modelInfo[model] : nil,
            host: endpoint.id == .openrouter ? settings.openRouterHost(for: model) : nil
        ))
    }

    /// Prices what this run's requests used (the speech-to-text of every chunk sent, and every clean-up answer, used or
    /// not, with the hedge's cancelled requests) and adds it, with the dictation's words once it's finished, to the Home
    /// page totals. `discarded`: clean-ups done ahead of time for a transcript the speaker then went on from.
    private func account(_ record: inout DictationRecord, pipeline: TranscriptionPipeline, polished: SegmentedPolishResult?,
                         discarded: [SegmentedPolishResult]) {
        let prices = PriceStore.shared
        let transcription = CostEstimator.transcriptions(pipeline.requestUsages(),
                                                         price: prices.price(settings.sttProvider, settings.sttModel),
                                                         preferReported: settings.sttProvider == .openrouter)
        // Chunks the backup speech-to-text route answered are priced with its model.
        let backup = CostEstimator.transcriptions(pipeline.backupRequestUsages(),
                                                  price: prices.price(settings.sttBackupProvider, settings.sttBackupModel),
                                                  preferReported: settings.sttBackupProvider == .openrouter)
        let transcriptionCost = transcription.cost + backup.cost
        var unpriced = transcription.unpriced + backup.unpriced
        var cleanup = 0.0
        func add(_ usage: RequestUsage?, provider: ProviderID, model: String) {
            guard let usage else { return }
            if let cost = CostEstimator.chat(usage, price: prices.price(provider, model), preferReported: provider == .openrouter) {
                cleanup += cost
            } else {
                unpriced += 1
            }
        }
        for answer in (discarded + [polished].compactMap { $0 }).flatMap(\.answers) {
            add(answer.result.usage, provider: answer.provider, model: answer.model)
            for request in answer.cancelled { add(request.usage, provider: request.provider, model: request.model) }
        }
        if transcriptionCost + cleanup > 0 { record.cost = (record.cost ?? 0) + transcriptionCost + cleanup }
        note(String(format: "cost: transcription $%.6f, clean-up $%.6f, unpriced requests %d", transcriptionCost, cleanup, unpriced))
        UsageStore.shared.record(&record, transcriptionCost: transcriptionCost, cleanupCost: cleanup, unpriced: unpriced)
    }

    // MARK: - Retry / cancel

    /// Re-runs a saved dictation. Chunks that already succeeded are reused; only failed ones are re-sent.
    func retry(_ saved: DictationRecord, paste: Bool) {
        guard state == .idle else { return }
        guard let endpoint = settings.sttEndpoint else { return }
        guard saved.hasAudio, let data = try? Data(contentsOf: saved.audioURL),
              let samples = WAV.decodeSamples(data) else {
            hud.show(.error(L("找不到这条记录的录音文件", "The recording for this entry is gone")), autoHideAfter: 3)
            return
        }
        deliverByPaste = paste
        // A cancelled recording still being transcribed in the background is this run's now.
        keptInBackground.remove(saved.id)
        var record = saved
        record.status = .processing
        record.error = nil
        // Saved transcripts are reused by chunk index, so only when the recording is cut into the same chunks again.
        let preset = saved.chunkLayout == Chunker.layout ? saved.chunkTexts : [:]
        record.chunkLayout = Chunker.layout
        history.update(record)
        self.record = record
        state = .processing
        hud.showWorking(polishes: settings.polishEnabled)

        let pipeline = makePipeline(endpoint: endpoint, preset: preset)
        self.pipeline = pipeline
        let generation = self.generation
        processingTask = Task { [weak self] in
            // Let the menu close and focus return to the previous app before pasting.
            try? await Task.sleep(nanoseconds: 300_000_000)
            pipeline.append(samples)
            await self?.process(record: record, pipeline: pipeline, generation: generation)
        }
    }

    func cancel(silently: Bool) {
        let wasRecording = state == .recording
        processingTask?.cancel()
        pipeline?.cancel()
        // Also right after the key was released, while the last of the audio is still coming in: no waiting for it.
        if isCapturing { stopCapture() }
        if wasRecording {
            if let record { history.delete(record) }
        } else if var record {
            // Cancelled while processing: keep what was transcribed; it can be re-transcribed for a day.
            if let pipeline {
                record.chunkTexts = pipeline.completedTranscripts()
                if record.rawText.isEmpty {
                    record.rawText = TranscriptJoiner.join(record.chunkTexts.sorted { $0.key < $1.key }.map(\.value))
                }
            }
            record.status = .cancelled
            history.update(record)
        }
        reset()
        if silently { hud.show(.hidden) } else { hud.show(.error(L("已取消", "Cancelled")), autoHideAfter: 1) }
    }

    /// Quitting mid-recording keeps the dictation instead of losing it: capture stops, the recording file is closed (so
    /// it's complete) and the record stays in History, where the next launch shows it as interrupted with its audio saved,
    /// ready to retry. Runs while the app terminates, so it only does what's quick; a dictation already being processed
    /// is left as it is, for the next launch to show the same way.
    func stopForQuit() {
        guard isCapturing, var record else { return }
        processingTask?.cancel()
        pipeline?.cancel()
        stopCapture()
        record.duration = counter.seconds
        // A retry reuses the segments already transcribed.
        if let pipeline { record.chunkTexts = pipeline.completedTranscripts() }
        // An accidental tap isn't worth an entry.
        if record.duration - preRollSeconds < 0.4 { history.delete(record) } else { history.update(record) }
    }

    private func abort(message: String) {
        guard state == .recording else { return }
        // Keep whatever was captured: process it rather than throwing it away.
        hud.show(.error(message), autoHideAfter: 2)
        finishRecording()
    }

    private func finish(showing phase: HUDModel.Phase, hideAfter: Double) {
        reset()
        hud.show(phase, autoHideAfter: hideAfter)
    }

    private func reset() {
        dropSpeculativePolish()
        generation += 1
        state = .idle
        preRollSeconds = 0
        pipeline = nil
        record = nil
        pressedAt = nil
        pasteTarget = nil
        handsFree = false
        processingTask = nil
    }

    // MARK: - Misc

    /// OpenRouter model metadata, used to turn reasoning off for the clean-up models.
    func refreshModelInfo() {
        let usesOpenRouter = settings.polishProvider == .openrouter
            || (settings.polishBackupEnabled && settings.polishBackupProvider == .openrouter)
        guard usesOpenRouter, let endpoint = settings.endpoint(for: .openrouter) else { return }
        Task { [weak self] in
            guard let models = try? await APIClient(endpoint: endpoint).listModels() else { return }
            self?.modelInfo = Dictionary(models.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        }
    }

    private func playSound(_ name: String) {
        guard settings.playSounds, let sound = NSSound(named: name) else { return }
        sound.volume = 0.35
        sound.play()
    }
}
