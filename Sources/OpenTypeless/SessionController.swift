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
    private let settings = AppSettings.shared
    private let history = HistoryStore.shared
    private let recorder = AudioRecorder()

    private var pipeline: TranscriptionPipeline?
    private var writer: WAVFileWriter?
    private var record: DictationRecord?
    private var counter = SampleCounter()
    private var pressedAt: Date?
    private var handsFree = false
    private var processingTask: Task<Void, Never>?
    private var maxDurationTimer: Timer?
    private var modelInfo: [String: APIClient.ModelInfo] = [:]
    /// False when retrying from the history window: the result goes to the clipboard instead.
    private var deliverByPaste = true

    /// A press shorter than this toggles hands-free mode instead of push-to-talk.
    private let tapThreshold: TimeInterval = 0.35

    init() {
        recorder.onFailure = { [weak self] error in
            Task { @MainActor in
                self?.abort(message: L("录音中断：", "Recording interrupted: ") + error.localizedDescription)
            }
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
        cancel(silently: false)
    }

    // MARK: - Recording

    private func startRecording() {
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

        let pipeline = makePipeline(endpoint: sttEndpoint, preset: [:])
        let counter = SampleCounter()
        let hudModel = hud.model
        recorder.onSamples = { samples in
            writer.append(samples)
            pipeline.append(samples)
            counter.add(samples.count)
        }
        recorder.onLevel = { level in
            DispatchQueue.main.async { hudModel.push(level: level) }
        }

        do {
            try recorder.start()
        } catch {
            writer.close()
            history.delete(record)
            hud.show(.error(L("无法开始录音：", "Can't start recording: ") + error.localizedDescription), autoHideAfter: 4)
            return
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
    }

    private func makePipeline(endpoint: ProviderEndpoint, preset: [Int: String]) -> TranscriptionPipeline {
        let language = settings.sttLanguage.isEmpty ? nil : settings.sttLanguage
        return TranscriptionPipeline(
            client: APIClient(endpoint: endpoint),
            options: .init(model: settings.sttModel, language: language),
            preset: preset
        )
    }

    private func stopCapture() {
        maxDurationTimer?.invalidate()
        maxDurationTimer = nil
        recorder.stop()
        recorder.onSamples = nil
        recorder.onLevel = nil
        writer?.close()
        writer = nil
    }

    private func finishRecording() {
        guard state == .recording, var record, let pipeline else { return }
        stopCapture()
        playSound("Pop")

        record.duration = counter.seconds
        if record.duration < 0.4 {
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
        hud.show(.working)

        processingTask = Task { [weak self] in
            await self?.process(record: record, pipeline: pipeline)
        }
    }

    // MARK: - Processing

    private func process(record initial: DictationRecord, pipeline: TranscriptionPipeline) async {
        var record = initial
        do {
            let raw = try await pipeline.finish()
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = raw
            record.error = nil
            guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                record.status = .done
                history.update(record)
                finish(showing: .error(L("没有识别到语音", "No speech detected")), hideAfter: 2)
                return
            }
            history.update(record)
            await polishAndDeliver(record: &record)
        } catch let failure as PipelineFailure {
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = failure.partialText
            record.status = .failed
            record.error = failure.localizedDescription
            history.update(record)
            lastError = failure.localizedDescription
            finish(showing: .error(L("转写失败，可在菜单栏重试", "Failed — retry from the menu bar")), hideAfter: 4)
        } catch {
            if APIError.from(error) == .cancelled { return }
            record.status = .failed
            record.error = error.localizedDescription
            history.update(record)
            lastError = error.localizedDescription
            finish(showing: .error(error.localizedDescription), hideAfter: 5)
        }
    }

    private func polishAndDeliver(record: inout DictationRecord) async {
        var text = record.rawText
        var notice: String?

        if settings.polishEnabled {
            do {
                let result = try await polish(record.rawText)
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
        history.update(record)

        guard !Task.isCancelled else { return }
        await deliver(text, notice: notice)
    }

    /// Pastes at the cursor when a text input has focus; otherwise leaves the text on the clipboard.
    /// When focus can't be determined (apps without accessibility info) it pastes *and* keeps the
    /// text on the clipboard, so nothing is lost either way.
    private func deliver(_ text: String, notice: String?) async {
        let target = deliverByPaste ? FocusProbe.focusedTarget() : .notEditable
        note("deliver → \(target) in \(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "?")")
        if target == .notEditable {
            TextInserter.copyToClipboard(text)
            finish(showing: notice.map { .error($0) } ?? .copied, hideAfter: 1.6)
            return
        }
        let inserted = await TextInserter.insert(text, restoreClipboard: target == .editable && settings.restoreClipboard)
        if !inserted {
            TextInserter.copyToClipboard(text)
            finish(showing: .copied, hideAfter: 1.6)
        } else if let notice {
            finish(showing: .error(notice), hideAfter: 3)
        } else {
            finish(showing: .hidden, hideAfter: 0)
        }
    }

    private func polish(_ raw: String) async throws -> APIClient.PolishResult {
        guard let endpoint = settings.polishEndpoint, settings.isConfigured(settings.polishProvider) else {
            throw APIError.missingAPIKey(settings.polishProvider.displayName)
        }
        let options = PolishOptions(
            model: settings.polishModel,
            vocabulary: settings.vocabularyList,
            extraInstructions: settings.extraInstructions,
            modelInfo: endpoint.id == .openrouter ? modelInfo[settings.polishModel] : nil
        )
        let client = APIClient(endpoint: endpoint)
        return try await RetryPolicy(maxAttempts: 2).run { _ in
            try await client.polish(transcript: raw, options: options)
        }
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
        hud.show(.working)

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
            record.status = .failed
            record.error = L("已取消", "Cancelled")
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
        state = .idle
        pipeline = nil
        record = nil
        pressedAt = nil
        handsFree = false
        processingTask = nil
    }

    // MARK: - Misc

    /// OpenRouter model metadata, used to turn reasoning off for the clean-up model.
    func refreshModelInfo() {
        guard settings.polishProvider == .openrouter, let endpoint = settings.polishEndpoint else { return }
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
