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
    private var pressedAt: Date?
    private var handsFree = false
    private var processingTask: Task<Void, Never>?
    private var maxDurationTimer: Timer?
    /// Keeps the connections warm while recording (see `warmConnections`).
    private var warmTimer: Timer?
    private let preconnector = Preconnector()
    /// A clean-up started before the key was released, on the transcript a speculative tail gave.
    private var speculativePolish: SpeculativePolish?
    /// Clean-ups started ahead of time that finished but were then dropped because the speaker went on: they were billed too.
    private var discardedPolishes: [HedgedPolishResult] = []
    private var modelInfo: [String: APIClient.ModelInfo] = [:]
    /// False when retrying from the history window: the result goes to the clipboard instead.
    private var deliverByPaste = true

    /// A press shorter than this toggles hands-free mode instead of push-to-talk.
    private let tapThreshold: TimeInterval = 0.35

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
    }

    private var chosenDeviceUID: String? { settings.microphoneUID.isEmpty ? nil : settings.microphoneUID }

    /// With "Keep microphone ready" on, keeps the recorder running between dictations (on the chosen input), so a
    /// dictation starts at once and includes the moment before the key; otherwise lets it stop. Called at launch,
    /// when settings change and after each dictation.
    func updateWarmMicrophone() {
        guard state == .idle else { return }
        if settings.keepMicrophoneWarm, Permissions.microphoneGranted {
            if recorder.isRecording, openDeviceUID == chosenDeviceUID { return }
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
        }
    }

    // MARK: - Hotkey events

    private let log = Logger(subsystem: "local.opentypeless.app", category: "session")
    private func note(_ text: String) { log.debug("\(text, privacy: .public)") }

    func hotkeyPressed() {
        switch state {
        case .idle: note("start recording")
        case .recording where handsFree: note("stop (hands-free)")
        case .recording: note("press ignored — already recording")
        case .processing: note("press ignored — still processing the previous dictation")
        }
        switch state {
        case .idle:
            startRecording()
        case .recording where handsFree:
            finishRecording()
        default:
            break
        }
    }

    func hotkeyReleased() {
        guard state == .recording, !handsFree, let pressedAt else { return }
        let held = Date().timeIntervalSince(pressedAt)
        note(held < tapThreshold ? String(format: "tap (%.2fs) → hands-free", held) : String(format: "released after %.2fs → stop", held))
        if held < tapThreshold {
            handsFree = true
        } else {
            finishRecording()
        }
    }

    /// Fn+F-key and similar combos shouldn't start a dictation.
    func otherKeyPressed() {
        guard state == .recording, !handsFree, let pressedAt,
              Date().timeIntervalSince(pressedAt) < 1.0 else { return }
        note("cancelled — another key was pressed with the shortcut (treated as a key combo)")
        cancel(silently: true)
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
        reset()
        hud.show(.error(L("已取消，录音在历史记录中保留 24 小时", "Cancelled — kept in History for 24 hours")), autoHideAfter: 2.5)

        let kept = record
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
            guard let self, self.history.records.contains(where: { $0.id == record.id }) else { return }
            record.rawText = raw
            record.chunkTexts = pipeline.completedTranscripts()
            self.account(&record, pipeline: pipeline, polished: nil)
            self.history.update(record)
        }
    }

    // MARK: - Recording

    private func startRecording() {
        // First of all, so the connections are ready by the time the first request goes out.
        warmConnections()
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

        var record = history.create()
        record.status = .recording
        history.update(record)

        let writer: WAVFileWriter
        do {
            writer = try WAVFileWriter(url: record.audioURL)
        } catch {
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
        pressedAt = Date()
        handsFree = false
        state = .recording
        lastError = nil
        hud.model.resetLevels()
        hud.model.startedAt = Date()
        hud.show(.recording)
        playSound("Tink")

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
             options: APIClient.TranscriptionOptions(model: settings.sttBackupModel.trimmingCharacters(in: .whitespaces), language: language))
        }
        let key = ModelPrice.key(settings.sttProvider, settings.sttModel)
        let latency = latencies[key] ?? TranscriptionLatency()
        latencies[key] = latency
        return TranscriptionPipeline(
            client: APIClient(endpoint: endpoint),
            options: .init(model: settings.sttModel, language: language),
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
        writer = nil
    }

    private func finishRecording() {
        guard state == .recording, var record, let pipeline else { return }
        stopCapture()
        playSound("Pop")

        record.duration = counter.seconds
        if record.duration - preRollSeconds < 0.4 {
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
        hud.showWorking(polishes: settings.polishEnabled)

        processingTask = Task { [weak self] in
            await self?.process(record: record, pipeline: pipeline)
        }
    }

    // MARK: - Processing

    private func process(record initial: DictationRecord, pipeline: TranscriptionPipeline) async {
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
            bar.reach(.transcribed)
            record.timing = DictationRecord.Timing(transcription: ProcessInfo.processInfo.systemUptime - started)
            let backupChunks = pipeline.backupChunkCount()
            if backupChunks > 0 { record.timing?.transcriptionBackupChunks = backupChunks }
            if pipeline.tailWasSpeculative {
                note(String(format: "last segment transcribed ahead of time: text %.2fs after release",
                            ProcessInfo.processInfo.systemUptime - started))
            }
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = raw
            record.error = nil
            guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                record.status = .done
                account(&record, pipeline: pipeline, polished: nil)
                history.update(record)
                finish(showing: .error(L("没有识别到语音", "No speech detected")), hideAfter: 2)
                return
            }
            await polishAndDeliver(record: &record, pipeline: pipeline)
        } catch let failure as PipelineFailure {
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = failure.partialText
            record.status = .failed
            record.error = failure.localizedDescription
            account(&record, pipeline: pipeline, polished: nil)
            history.update(record)
            lastError = failure.localizedDescription
            finish(showing: .error(L("转写失败，可在菜单栏重试", "Failed — retry from the menu bar")), hideAfter: 4)
        } catch {
            if APIError.from(error) == .cancelled { return }
            record.status = .failed
            record.error = error.localizedDescription
            account(&record, pipeline: pipeline, polished: nil)
            history.update(record)
            lastError = error.localizedDescription
            finish(showing: .error(error.localizedDescription), hideAfter: 5)
        }
    }

    private func polishAndDeliver(record: inout DictationRecord, pipeline: TranscriptionPipeline) async {
        var text = record.rawText
        var notice: String?
        var polished: HedgedPolishResult?

        if settings.polishEnabled {
            // When the speaker paused before letting go and said nothing more, the clean-up of this very transcript
            // started while they were still holding the key: take it over (often it's already done).
            let polishing: Task<HedgedPolishResult, Error>
            var ahead = 0.0
            // One that already failed (while recording) gets a fresh attempt instead.
            if let speculative = speculativePolish, speculative.raw == record.rawText, !speculative.outcome.failed {
                speculativePolish = nil
                polishing = speculative.task
                // What it streams from now on moves this dictation's bar.
                speculative.partials.target = onPolishPartial(expected: record.rawText)
                ahead = ProcessInfo.processInfo.systemUptime - speculative.startedAt
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
                let result = outcome.result
                // The wait after the key was released: a clean-up started ahead of time had a head start.
                let firstToken = max(0, outcome.firstTokenSeconds - ahead)
                let total = max(0, outcome.totalSeconds - ahead)
                record.timing?.polishFirstToken = firstToken
                record.timing?.polish = total
                record.timing?.polishModel = outcome.model
                record.timing?.usedBackup = outcome.usedBackup
                note(String(format: "polish: %@%@, first token %.2fs, total %.2fs", outcome.model,
                            outcome.usedBackup ? " (backup)" : "", firstToken, total))
                if result.truncated {
                    notice = L("未整理，已插入原文", "Inserted without clean-up")
                    record.status = .polishFailed
                } else if Prompts.looksLikeAnAnswer(input: record.rawText, output: result.text) {
                    notice = L("未整理，已插入原文", "Inserted without clean-up")
                    record.status = .polishFailed
                } else {
                    text = result.text
                    record.polishedText = result.text
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

        if !Task.isCancelled {
            hud.model.bar.reach(.delivered)
            await deliver(text, notice: notice)
        }
        // Costs, the usage ledger and History are written once the text is in place: nobody is waiting on them.
        account(&record, pipeline: pipeline, polished: polished, discarded: discardedPolishes)
        discardedPolishes = []
        history.update(record)
    }

    // MARK: - Clean-up ahead of time

    /// A clean-up of `raw`, started while recording.
    private struct SpeculativePolish {
        let raw: String
        let startedAt: TimeInterval
        let task: Task<HedgedPolishResult, Error>
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
        var result: HedgedPolishResult?
        var failed = false
    }

    /// The pipeline has the whole transcript the recording would give if it ended now (the speaker paused), or no longer
    /// has one (they went on). Starts cleaning that transcript up, so the text can be ready when the key is released.
    private func speculationChanged(_ raw: String?) {
        guard state == .recording, settings.polishEnabled else { return }
        if let raw, raw == speculativePolish?.raw { return }
        dropSpeculativePolish()
        guard let raw, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        let outcome = PolishOutcome()
        let partials = PartialRelay()
        let task = Task { [weak self] () async throws -> HedgedPolishResult in
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

    /// Moves the bar as the clean-up streams in: its first token, then its length against the transcript's.
    private func onPolishPartial(expected transcript: String) -> @Sendable (String) -> Void {
        let expected = transcript.utf16.count
        let bar = hud.model.bar
        return { text in
            let received = text.utf16.count
            DispatchQueue.main.async { bar.reach(.polishing(received: received, expected: expected)) }
        }
    }

    /// Pastes at the cursor unless focus is clearly not a text input. Whether the paste actually
    /// landed is detected from the target app reading the clipboard: if it did, the user's previous
    /// clipboard is restored; if not, the text stays on the clipboard and the HUD says so.
    private func deliver(_ text: String, notice: String?) async {
        let target = deliverByPaste ? FocusProbe.focusedTarget() : .notEditable
        if target == .notEditable {
            note("deliver → clipboard (focus not editable) in \(frontmostID)")
            TextInserter.copyToClipboard(text)
            finish(showing: notice.map { .error($0) } ?? .copied, hideAfter: 1.6)
            return
        }
        let outcome = await TextInserter.insert(text, restoreClipboard: settings.restoreClipboard)
        note("deliver → \(outcome) (focus \(target)) in \(frontmostID)")
        switch outcome {
        case .pasted:
            if let notice {
                finish(showing: .error(notice), hideAfter: 3)
            } else {
                reset()
                hud.finishWorking()
            }
            if settings.learnFromEdits { editWatcher.watch(inserted: text) }
        case .notPasted:
            finish(showing: .copied, hideAfter: 1.6)
        case .noPermission:
            finish(showing: .error(L("没有辅助功能权限，文字已复制到剪贴板",
                                     "No Accessibility permission — text copied to the clipboard")), hideAfter: 3)
        }
    }

    private var frontmostID: String { NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "?" }

    private func polish(_ raw: String, onPartial: (@Sendable (String) -> Void)? = nil) async throws -> HedgedPolishResult {
        guard let endpoint = settings.polishEndpoint, settings.isConfigured(settings.polishProvider) else {
            throw APIError.missingAPIKey(settings.polishProvider.displayName)
        }
        let primary = route(endpoint, model: settings.polishModel)
        let backup = settings.polishBackupEndpoint.map { route($0, model: settings.polishBackupModel) }
        return try await RetryPolicy(maxAttempts: 2).run { _ in
            try await HedgedPolish.run(transcript: raw, primary: primary, backup: backup, onPartial: onPartial)
        }
    }

    private func route(_ endpoint: ProviderEndpoint, model: String) -> PolishRoute {
        let model = model.trimmingCharacters(in: .whitespaces)
        return PolishRoute(client: APIClient(endpoint: endpoint), options: PolishOptions(
            model: model,
            vocabulary: settings.vocabularyList,
            misheard: settings.misheardHints,
            extraInstructions: settings.extraInstructions,
            modelInfo: endpoint.id == .openrouter ? modelInfo[model] : nil
        ))
    }

    /// Prices what this run's requests used (the speech-to-text of every chunk sent, and the clean-up answer that was
    /// kept) and adds it, with the dictation's words once it's finished, to the Home page totals.
    /// `discarded`: clean-ups done ahead of time for a transcript the speaker then went on from.
    private func account(_ record: inout DictationRecord, pipeline: TranscriptionPipeline, polished: HedgedPolishResult?,
                         discarded: [HedgedPolishResult] = []) {
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
        for answer in discarded + [polished].compactMap({ $0 }) {
            guard let usage = answer.result.usage else { continue }
            if let cost = CostEstimator.chat(usage, price: prices.price(answer.provider, answer.model),
                                             preferReported: answer.provider == .openrouter) {
                cleanup += cost
            } else {
                unpriced += 1
            }
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
        var record = saved
        record.status = .processing
        record.error = nil
        history.update(record)
        self.record = record
        state = .processing
        hud.showWorking(polishes: settings.polishEnabled)

        let pipeline = makePipeline(endpoint: endpoint, preset: saved.chunkTexts)
        self.pipeline = pipeline
        processingTask = Task { [weak self] in
            // Let the menu close and focus return to the previous app before pasting.
            try? await Task.sleep(nanoseconds: 300_000_000)
            pipeline.append(samples)
            await self?.process(record: record, pipeline: pipeline)
        }
    }

    func cancel(silently: Bool) {
        let wasRecording = state == .recording
        processingTask?.cancel()
        pipeline?.cancel()
        if wasRecording {
            stopCapture()
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
        state = .idle
        preRollSeconds = 0
        pipeline = nil
        record = nil
        pressedAt = nil
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
