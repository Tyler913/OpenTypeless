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

/// What a recording is for: plain dictation, a translation (see `TranslationTarget`), or an edit of the text selected
/// in the app in front (see `SelectionCommand`), which the translation and command chords (see `ModeChords`) switch it
/// to while recording.
enum RecordingMode: Equatable, Sendable {
    case dictation
    case translate
    case command
}

private extension PolishFallback {
    /// Why a translation couldn't be used, for History.
    var translationReason: String {
        switch self {
        case .truncated: return L("翻译被截断了", "the translation was cut off")
        case .answered: return L("模型回答了内容，没有翻译", "the model answered instead of translating")
        case .summarised: return L("翻译漏掉了太多内容", "the translation left too much out")
        case .untranslated: return L("结果不是目标语言", "it didn't come back in the target language")
        case .failed, .unrelated, .copied: return L("没有可用的翻译", "no usable translation came back")
        }
    }

    /// Why an edit of a selection couldn't be used, for History and the capsule.
    var commandReason: String {
        switch self {
        case .truncated: return L("改写被截断了", "the rewrite was cut off")
        case .answered: return L("模型回复了别的内容，没有改写", "the model replied instead of editing")
        default: return L("没有可用的改写", "no usable rewrite came back")
        }
    }
}

extension SelectionRefusal {
    /// What the capsule says when the selection can't be edited by voice.
    var message: String {
        switch self {
        case .noSelection:
            return L("没有选中文字：先选中要修改的文字，再按住听写快捷键说出怎么改",
                     "No text selected — select the text to change, then hold the dictation shortcut and say how")
        case .terminal: return L("终端里的文字不能用语音编辑", "Text in a terminal can't be edited by voice")
        case .secureField: return L("密码框不能用语音编辑", "Password fields can't be edited by voice")
        case .unreadableInEditor:
            return L("读不到这个编辑器里选中的文字", "Can't read the selection in this editor")
        case let .tooLong(count):
            return L("选中的文字太长（\(count) 字符，最多 \(SelectionCommand.maxCharacters)）",
                     "The selection is too long (\(count) characters; up to \(SelectionCommand.maxCharacters))")
        }
    }
}

extension SelectionCommand.ClipboardReason {
    /// Why an edit went to the clipboard instead of over the selection.
    var message: String {
        switch self {
        case .locked: return L("屏幕已锁定，改写后的文字已复制", "Screen locked — the edited text is on the clipboard")
        case .movedAway:
            return L("已切换到别的窗口，改写后的文字已复制", "You moved to another window — the edited text is on the clipboard")
        case .selectionChanged:
            return L("选中的文字变了，改写后的文字已复制", "The selection changed — the edited text is on the clipboard")
        case .notEditable:
            return L("这里不能粘贴，改写后的文字已复制", "Can't paste there — the edited text is on the clipboard")
        }
    }
}

/// Whether a clean-up request carried the text around the cursor (set as the requests go out, read on main), for History.
final class ContextSent: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    func mark() { lock.withLock { value = true } }
    var isSet: Bool { lock.withLock { value } }
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
    /// What the running dictation goes by: the global settings with the profile of the app it was started in, taken once
    /// when it started (see DictationSettings).
    private var dictationSettings: DictationSettings?
    private var counter = SampleCounter()
    /// When the hotkey went down, by the key event's own time (`ProcessInfo.systemUptime` clock, see `HotkeyPress`).
    private var pressedAt: TimeInterval?
    private var handsFree = false
    /// Second keys that switch the recording's mode while the hotkey is held (see `ModeChords`): the translation chord's,
    /// while translation mode is on (see `updateModeChords`, which also gives the hotkey monitor the keys to keep from
    /// the focused app).
    private(set) var modeChords = ModeChords<RecordingMode>()
    /// What the running recording is for; plain dictation at every start.
    private(set) var mode: RecordingMode = .dictation
    /// Command mode: the selection being read (or read) when the chord was pressed, and where (see `readSelection`).
    /// Dropped with the recording, or when another chord switches it to another mode.
    private var selectionRead: Task<SelectionReading, Never>?

    /// What reading the selection found: where (nil when this app was in front), what, and how many clicks and keys had
    /// reached apps by the time it was read.
    private struct SelectionReading {
        let place: SelectedText.Place?
        let read: SelectionRead
        let mark: SelectedText.InputMark
    }
    /// The "Paste last dictation" waiting for its keys to be let go, or pasting, by its number. A new dictation, Esc or
    /// the screen locking drops it (nil), and what it still had to do is left undone. Pressing it again meanwhile does nothing.
    private var pendingPasteLast: Int?
    private var pasteLastCount = 0
    /// The app last in front other than this one: where the menu-bar panel's "Paste last dictation" goes, should the
    /// click on the panel have brought this app forward.
    private var lastOtherApp: pid_t?
    /// Which dictation is current: bumped whenever one ends or is cancelled, so work still out for an earlier one (its
    /// paste, its Return, its reset) can tell it's no longer wanted and leaves the next dictation alone.
    private var generation = 0
    /// The app in front when the recording stopped, where the text is meant to go (for a retry, the app "Paste last
    /// dictation" was asked for in, or nil).
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
    /// The text around the cursor in the field being dictated into, read as recording started, for the clean-up to spell
    /// names by (see CursorContext); nil when that's off or there was none. Never saved.
    private var cursorContext: String?
    /// That read, while it's still under way.
    private var cursorRead: CursorContextRead?
    /// Whether this dictation's clean-up requests carried that text, for History. A new one for each dictation: work still
    /// out for an earlier one holds on to its own.
    private var contextSent = ContextSent()
    /// Audio and video files being transcribed into History, alongside (never instead of) dictating.
    private(set) lazy var files = FileTranscriber(controller: self)

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
        lastOtherApp = SessionController.otherApp(NSWorkspace.shared.frontmostApplication)
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
                                                          object: nil, queue: .main) { [weak self] note in
            let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            MainActor.assumeIsolated {
                if let other = SessionController.otherApp(app) { self?.lastOtherApp = other }
            }
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

    /// Another key went down while the hotkey was held: a registered chord switches the recording's mode; otherwise
    /// Fn+F-key and similar combos shouldn't start a dictation.
    func otherKeyPressed(_ keyCode: Int64, at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        guard state == .recording, let pressedAt else { return }
        switch modeChords.otherKey(keyCode, pressedAt: pressedAt, at: time, handsFree: handsFree) {
        case let .switchMode(newMode):
            guard newMode != mode else { return }
            note("mode → \(newMode) (chord key \(keyCode))")
            switchMode(to: newMode)
        case .cancel:
            note("cancelled — another key was pressed with the shortcut (treated as a key combo)")
            cancel(silently: true)
        case .ignore:
            break
        }
    }

    /// The running recording becomes `newMode`'s: the record keeps it (a retry runs the same way), the capsule shows it,
    /// and a clean-up started ahead of time with the other prompt starts again with this one. Command mode reads the
    /// selection now, while the app it's in is in front (see `readSelection`).
    private func switchMode(to newMode: RecordingMode) {
        let translation = newMode == .translate ? settings.translationTarget : nil
        // Translation or command mode turned off a moment ago: its chord is on its way out.
        switch newMode {
        case .dictation: break
        case .translate: guard translation != nil else { return }
        case .command: guard settings.commandModeEnabled else { return }
        }
        if mode == .command { dropSelection() }
        mode = newMode
        record?.translation = translation
        if newMode == .command { record?.mode = SelectionCommand.recordMode }
        hud.model.badge = translation?.badge ?? (newMode == .command ? L("✎ 编辑", "✎ Edit") : nil)
        if newMode == .command { readSelection() }
        if let raw = speculativePolish?.raw {
            dropSpeculativePolish()
            // Never for an edit, which only starts once its instruction is complete.
            speculationChanged(raw)
        }
    }

    /// Command mode: reads the selection in the app in front, the one the edit goes back to (see `SelectedText`). One
    /// that can't be edited (none, in a password field or a terminal, too long) stops the recording at once and says
    /// why; nothing is pasted or copied. Read by ⌘C, it's only known once the keys are let go of, after a push-to-talk
    /// recording: then `editSelection` says so.
    private func readSelection() {
        let generation = self.generation
        // The app in front as the chord goes down.
        let front = NSWorkspace.shared.frontmostApplication
        let hotkey = settings.hotkey
        let timeout = TimeInterval(max(1, settings.maxRecordingMinutes) * 60 + 10)
        selectionRead = Task { [weak self] in
            guard let place = await SelectedText.Place.front(front) else {
                self?.selectionRefused(.noSelection, generation: generation)
                return SelectionReading(place: nil, read: .refused(.noSelection), mark: .now())
            }
            let read = await SelectedText.read(at: place, releasing: hotkey, releaseTimeout: timeout)
            let mark = SelectedText.InputMark.now()
            if case let .refused(reason) = read { self?.selectionRefused(reason, generation: generation) }
            return SelectionReading(place: place, read: read, mark: mark)
        }
    }

    /// While still recording in command mode: the recording stops as if it never started, and the capsule says why.
    private func selectionRefused(_ reason: SelectionRefusal, generation: Int) {
        guard generation == self.generation, state == .recording, mode == .command else { return }
        note("command mode: selection refused (\(reason)) — recording stopped")
        cancel(silently: true)
        hud.show(.error(reason.message), autoHideAfter: 3)
    }

    private func dropSelection() {
        selectionRead?.cancel()
        selectionRead = nil
    }

    /// Registers the translation chord's keys while translation mode is on, and command mode's while that is (none
    /// otherwise), and returns every chord key,
    /// for the hotkey monitor to keep from the focused app while the hotkey is held. A modifier-only hotkey's own key is
    /// never its chord. At launch, and whenever settings change.
    func updateModeChords() -> Set<Int64> {
        var chords = ModeChords<RecordingMode>()
        let hotkey = settings.hotkey.isModifierOnly ? settings.hotkey.keyCode : nil
        let translating = settings.translationTarget != nil
        if translating {
            for key in settings.translationChord.keyCodes(excludingHotkey: hotkey) {
                chords.register(key, mode: .translate)
            }
        }
        if settings.commandModeEnabled {
            for key in SelectionCommand.chordKeys(command: settings.commandChord,
                                                  translation: translating ? settings.translationChord : nil,
                                                  excludingHotkey: hotkey) {
                chords.register(key, mode: .command)
            }
        }
        modeChords = chords
        return chords.keys
    }

    /// The Mac is going to sleep or the screen was locked. A hands-free recording mustn't keep the microphone open on
    /// the lock screen, nor its text land in whatever has focus afterwards: a recording stops as with Esc (a long one is
    /// kept in History), and one being processed goes to the clipboard instead of being pasted.
    func systemLocked(_ reason: String) {
        // A warm microphone doesn't stay on behind the lock screen either; see `systemUnlocked`.
        isLocked = true
        // Nor is the text around the cursor read on: what it would find now isn't the speaker's.
        stopCursorContextRead()
        defer {
            updateWarmMicrophone()
            silenceWarmMicrophoneIfLocked()
        }
        // Nor does a "Paste last dictation" still waiting go ahead once the screen is unlocked again.
        dropPendingPasteLast(reason)
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

    /// While locked, a warm microphone goes off as soon as no dictation is capturing, also mid-processing: kept warm, it
    /// would go on filling the pre-roll behind the lock screen until the text was in.
    private func silenceWarmMicrophoneIfLocked() {
        guard isLocked, !isCapturing, recorder.isRecording else { return }
        recorder.stop()
        openDeviceUID = nil
        sink.end()
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
        guard state != .idle else {
            dropPendingPasteLast("Esc")
            return
        }
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
        let sent = contextSent
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
                self.account(&record, pipeline: pipeline, polished: nil, discarded: discarded, context: sent)
                return
            }
            guard self.history.records.contains(where: { $0.id == record.id }) else { return }
            record.rawText = raw
            record.chunkTexts = pipeline.completedTranscripts()
            self.account(&record, pipeline: pipeline, polished: nil, discarded: discarded, context: sent)
            self.history.update(record)
        }
    }

    // MARK: - Recording

    /// `pressedAt` is the key event's own time: opening a cold microphone (a Bluetooth one can take a second or more)
    /// mustn't count against how long the key was held.
    private func startRecording(pressedAt: TimeInterval) {
        // The app in front now decides this dictation's settings, once: the text is spoken for it, so switching to another
        // app before the key is released doesn't change them (the paste still checks where focus went).
        let dictation = settings.dictationSettings(forApp: NSWorkspace.shared.frontmostApplication?.bundleIdentifier)
        // First of all, so the connections are ready by the time the first request goes out.
        warmConnections(polishes: dictation.polishEnabled)
        // Someone is dictating, so the screen is unlocked, whatever notification was missed (a wake without a lock).
        isLocked = false
        // The new dictation is what the user is after now.
        dropPendingPasteLast("a dictation started")
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
        record.profileID = dictation.profileID
        record.profileName = dictation.profileName
        record.chunkLanguage = dictation.language
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
        let pipeline = makePipeline(endpoint: sttEndpoint, preset: [:], language: dictation.language, onSpeculation: { [weak self] raw in
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
            livePreview?.start(language: dictation.language)
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
        dictationSettings = dictation
        if let name = dictation.profileName { note("app profile: \(name)") }
        deliverByPaste = true
        self.pressedAt = pressedAt
        handsFree = false
        mode = .dictation
        state = .recording
        lastError = nil
        hud.model.resetLevels()
        hud.model.startedAt = Date()
        hud.model.badge = nil
        hud.show(.recording)
        playSound("Tink")
        // Read now, while the field holds only what the speaker typed: the dictation goes in at the end.
        if dictation.polishEnabled, settings.useCursorContext { startCursorContextRead() }
        // After the start sound, which muting would cut off.
        if settings.muteWhileRecording { outputMute.mute(after: .milliseconds(settings.playSounds ? 300 : 0)) }

        maxDurationTimer = Timer.scheduledTimer(
            withTimeInterval: TimeInterval(max(1, settings.maxRecordingMinutes) * 60), repeats: false
        ) { [weak self] _ in
            Task { @MainActor in self?.finishRecording() }
        }
        // A long dictation keeps the clean-up's connection (idle while speech-to-text works) from timing out.
        warmTimer = Timer.scheduledTimer(withTimeInterval: 25, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.warmConnections(polishes: dictation.polishEnabled) }
        }
    }

    /// Starts reading the text around the cursor in the app in front, off the main thread. What comes back is taken only
    /// while the read is still this dictation's: not stopped by its end, by the screen locking, or by the clean-up taking
    /// what was in by then. Nothing waits for it.
    private func startCursorContextRead() {
        guard let app = NSWorkspace.shared.frontmostApplication else { return }
        let generation = self.generation
        cursorRead = CursorContextReader.start(pid: app.processIdentifier) { [weak self] read, context in
            guard let self, self.cursorRead === read, self.generation == generation, !self.isLocked else { return }
            self.cursorRead = nil
            self.cursorContext = context
            // Its length only: the text stays out of the log.
            self.note(context.map { "text around the cursor: \($0.count) characters" } ?? "no text around the cursor")
        }
    }

    /// Stops the read of the text around the cursor: whatever it finds is dropped.
    private func stopCursorContextRead() {
        cursorRead?.cancel()
        cursorRead = nil
    }

    /// Opens the connections this dictation's requests will use (speech-to-text and clean-up, main and backup routes)
    /// ahead of them, so the first request after the key is released doesn't wait for DNS, TCP and TLS. Each host once.
    /// `polishes`: whether this dictation is cleaned up (its app profile can say otherwise than the global setting).
    private func warmConnections(polishes: Bool) {
        var endpoints: [ProviderEndpoint?] = [settings.sttEndpoint, settings.sttBackupEndpoint]
        if polishes || settings.translationTarget != nil || settings.commandModeEnabled {
            endpoints += [settings.polishEndpoint, settings.polishBackupEndpoint]
        }
        preconnector.warm(endpoints.map { $0.map { APIClient(endpoint: $0) } })
    }

    /// How long each speech-to-text route takes, kept while the app runs, so the backup is asked when a chunk is late.
    private var latencies: [String: TranscriptionLatency] = [:]

    /// `language`: the dictation's spoken-language hint, "" to auto-detect; nil for the global setting (a file's).
    /// `forFile`: for a file being transcribed (see FileTranscriber), which sends fewer requests at once on a session of
    /// its own, has no speculative tail, and keeps its own idea of how long requests take (its chunks are mostly long).
    func makePipeline(endpoint: ProviderEndpoint, preset: [Int: String], language: String? = nil, forFile: Bool = false,
                      onSpeculation: (@Sendable (String?) -> Void)? = nil) -> TranscriptionPipeline {
        let spoken = language ?? settings.sttLanguage
        let language = spoken.isEmpty ? nil : spoken
        let backup = settings.sttBackupEndpoint.map {
            (client: forFile ? APIClient.forFiles(endpoint: $0) : APIClient(endpoint: $0),
             options: APIClient.TranscriptionOptions(model: settings.sttBackupModel.trimmingCharacters(in: .whitespaces), language: language,
                                                     vocabulary: settings.dictationVocabulary))
        }
        let key = ModelPrice.key(settings.sttProvider, settings.sttModel)
        let latency = forFile ? TranscriptionLatency() : latencies[key] ?? TranscriptionLatency()
        if !forFile { latencies[key] = latency }
        return TranscriptionPipeline(
            client: forFile ? APIClient.forFiles(endpoint: endpoint) : APIClient(endpoint: endpoint),
            options: .init(model: settings.sttModel, language: language, vocabulary: settings.dictationVocabulary),
            preset: preset,
            backup: backup,
            latency: latency,
            maxConcurrentRequests: forFile ? FileTranscription.maxConcurrentRequests : 3,
            speculate: !forFile,
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
        silenceWarmMicrophoneIfLocked()
    }

    /// How long capture goes on after the key is released, in nanoseconds. The microphone hands its audio over a block
    /// at a time: the block with the last syllable would otherwise be cut off when the tap is removed or, with a warm
    /// microphone, land in the pre-roll and be thrown away.
    private static let releaseTail: UInt64 = 100_000_000

    private func finishRecording() {
        guard state == .recording, var record, let pipeline, let dictation = dictationSettings else { return }
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
        // Released in an app with other settings than the one the recording was started in (see `suits`): the text was
        // spoken for that one, so it goes to the clipboard, with no Return.
        if !suits(dictation) {
            note("released in \(frontmostID), which has other settings than the app the recording started in → clipboard")
            deliverByPaste = false
        }
        hud.showWorking(polishes: dictation.polishEnabled || record.translation != nil || record.isCommand)

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
            await self.process(record: record, pipeline: pipeline, generation: generation, dictation: dictation)
        }
    }

    // MARK: - Processing

    private func process(record initial: DictationRecord, pipeline: TranscriptionPipeline, generation: Int,
                         dictation: DictationSettings) async {
        var record = initial
        let sent = contextSent
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
            // A new transcript supersedes a clean-up from an earlier run (a retry): History mustn't show that one if this
            // run inserts the raw text (clean-up off or failed). A retry that fails before this keeps the old result.
            record.polishedText = nil
            // And so does what its snippets put in: expanded again from this run's text, with the snippets as they are now.
            record.insertedText = nil
            record.error = nil
            guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                record.status = .done
                account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes(), context: sent)
                history.update(record)
                finish(showing: .error(L("没有识别到语音", "No speech detected")), hideAfter: 2)
                return
            }
            if record.isCommand {
                await editSelection(record: &record, pipeline: pipeline, generation: generation, dictation: dictation)
            } else {
                await polishAndDeliver(record: &record, pipeline: pipeline, generation: generation, dictation: dictation,
                                       contextSent: sent)
            }
        } catch let failure as PipelineFailure {
            guard isCurrent(generation) else { return }
            record.chunkTexts = pipeline.completedTranscripts()
            record.rawText = failure.partialText
            record.status = .failed
            record.error = failure.localizedDescription
            account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes(), context: sent)
            history.update(record)
            lastError = failure.localizedDescription
            // An edit can't be retried: its selection isn't kept.
            finish(showing: .error(record.isCommand
                                   ? L("转写失败，选中的文字没有改动", "Transcription failed — your text is unchanged")
                                   : L("转写失败，可在菜单栏重试", "Failed — retry from the menu bar")), hideAfter: 4)
        } catch {
            if APIError.from(error) == .cancelled || !isCurrent(generation) { return }
            record.status = .failed
            record.error = error.localizedDescription
            account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes(), context: sent)
            history.update(record)
            lastError = error.localizedDescription
            finish(showing: .error(error.localizedDescription), hideAfter: 5)
        }
    }

    private func polishAndDeliver(record: inout DictationRecord, pipeline: TranscriptionPipeline, generation: Int,
                                  dictation: DictationSettings, contextSent sent: ContextSent) async {
        let begun = ProcessInfo.processInfo.systemUptime
        var text = record.rawText
        var notice: String?
        var polished: SegmentedPolishResult?
        // A translation that couldn't be used, set aside for the clean-up: billed all the same.
        var setAside: SegmentedPolishResult?
        // The translation's (and clean-up's) answers as they come in, counted even if Esc then stops the clean-up.
        let ledger = PolishLedger()
        // The text going in is a translation: the user's edits to it say nothing about what speech recognition misheard.
        var translated = false
        let translation = record.translation

        if dictation.polishEnabled || translation != nil {
            // When the speaker paused before letting go and said nothing more, the clean-up (or translation) of this very
            // transcript started while they were still holding the key: take it over (often it's already done).
            let polishing: Task<SegmentedPolishResult, Error>
            var ahead = 0.0
            // What came in by now is what goes: a read still out is dropped, so no later request differs from this one.
            stopCursorContextRead()
            let context = cursorContext
            // One that already failed (while recording) gets a fresh attempt instead, and so does one started before the
            // text around the cursor was in.
            if let speculative = speculativePolish, speculative.raw == record.rawText, speculative.translation == translation,
               speculative.context == context,
               !speculative.outcome.failed {
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
                polishing = Task {
                    try await self.polish(raw, with: dictation, translation: translation, context: context, sent: sent,
                                          onPartial: onPartial)
                }
            }
            // Saved while the clean-up runs, so the transcript survives a crash.
            history.update(record)
            do {
                if let translation {
                    // The translation, or when it can't be used as it is, the clean-up (when that's on): one more request.
                    let raw = record.rawText
                    let onPartial = onPolishPartial(expected: raw)
                    var cleanUp: (@Sendable () async throws -> SegmentedPolishResult)?
                    if dictation.polishEnabled {
                        cleanUp = { [weak self] () async throws -> SegmentedPolishResult in
                            guard let self else { throw APIError.cancelled }
                            return try await self.polish(raw, with: dictation, translation: nil, context: context, sent: sent,
                                                         onPartial: onPartial)
                        }
                    }
                    let outcome = try await withTaskCancellationHandler {
                        try await TranslatedPolish.run(translate: { try await polishing.value }, cleanUp: cleanUp,
                                                       ledger: ledger)
                    } onCancel: {
                        polishing.cancel()
                    }
                    if outcome.translated, let translatedResult = outcome.translation {
                        polished = translatedResult
                        noteTiming(of: translatedResult, ahead: ahead, in: &record, what: "translation → \(translation.id)")
                        text = translatedResult.text
                        record.polishedText = translatedResult.text
                        record.status = .done
                        translated = true
                    } else {
                        setAside = outcome.translation
                        let fallback = outcome.translationFallback ?? .failed
                        let reason = outcome.translationError?.localizedDescription ?? fallback.translationReason
                        record.error = L("未能翻译：\(reason)", "Couldn't translate: \(reason)")
                        // The wait after the key was released, both requests together.
                        record.timing?.polish = ProcessInfo.processInfo.systemUptime - begun
                        record.timing?.polishModel = (outcome.cleanUp ?? outcome.translation)?.model
                        record.timing?.usedBackup = (outcome.cleanUp ?? outcome.translation)?.usedBackup
                        polished = outcome.cleanUp
                        if let cleaned = outcome.cleanUp, cleaned.fallbacks.count < cleaned.parts.count {
                            note("translation unusable (\(fallback.rawValue)) → clean-up inserted")
                            notice = L("未能翻译，已插入整理后的原文", "Couldn't translate — inserted the clean-up")
                            text = cleaned.text
                            record.polishedText = cleaned.text
                            record.status = .done
                        } else {
                            note("translation unusable (\(fallback.rawValue)) → inserted as spoken")
                            notice = L("未能翻译，已插入原文", "Couldn't translate — inserted as spoken")
                            record.status = .polishFailed
                        }
                    }
                } else {
                    let outcome = try await withTaskCancellationHandler {
                        try await polishing.value
                    } onCancel: {
                        polishing.cancel()
                    }
                    polished = outcome
                    noteTiming(of: outcome, ahead: ahead, in: &record, what: "polish")
                    let fallbacks = outcome.fallbacks
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
                }
            } catch {
                if APIError.from(error) == .cancelled {
                    // Esc while the clean-up ran after a translation that couldn't be used: nothing goes in, but that
                    // translation was billed. (`cancel` has kept the record as cancelled; so it stays, now with its cost.)
                    let billed = ledger.all
                    guard !billed.isEmpty else { return }
                    record.status = .cancelled
                    account(&record, pipeline: pipeline, polished: nil, discarded: billed, context: sent)
                    history.update(record)
                    return
                }
                notice = L("整理失败，已插入原文", "Clean-up failed — inserted raw text")
                record.error = error.localizedDescription
                record.status = .polishFailed
            }
        } else {
            record.status = .done
        }
        if record.status == .polishFailed { record.polishedText = nil }
        // Snippets go in here, in code, never through the clean-up: a model would paraphrase or translate a long expansion,
        // and its guards would take one for an answer. History keeps what was said (word counts go by it) and what went in.
        let expanded = SnippetExpander.expand(text, with: settings.snippets)
        // Scalar by scalar: an expansion that is the trigger in another composition is still a change (as on Windows).
        record.insertedText = expanded.unicodeScalars.elementsEqual(text.unicodeScalars) ? nil : expanded
        text = expanded

        // Taken now: once the text is in, the next dictation may already be collecting its own.
        let discarded = takeDiscardedPolishes() + (setAside.map { [$0] } ?? [])
        if isCurrent(generation) {
            hud.model.bar.reach(.delivered)
            let transcription = record.timing?.transcription ?? 0
            record.timing?.delivered = transcription + ProcessInfo.processInfo.systemUptime - begun
            record.timing?.delivery = await deliver(text, notice: notice, generation: generation, dictation: dictation,
                                                    watchEdits: !translated)
        }
        // Esc came before the text was in place (or while it went in): `cancel` has kept the record as cancelled, and
        // so it stays, now with its clean-up.
        if Task.isCancelled { record.status = .cancelled }
        // Costs, the usage ledger and History are written once the text is in place: nobody is waiting on them.
        account(&record, pipeline: pipeline, polished: polished, discarded: discarded, context: sent)
        history.update(record)
    }

    // MARK: - Editing a selection

    /// Command mode, once the instruction is transcribed: the selection read when the chord was pressed is edited by it
    /// (see `SelectionCommand`), and the rewrite pasted over it (see `deliverEdit`); an answer to a question about the
    /// text is shown and copied instead. Nothing is pasted when the selection couldn't be read, the rewrite can't be used
    /// (cut off, a refusal, nothing), or the model changed nothing; nor once Esc or the screen locking came first.
    /// `dictation`: the settings it was started with, whose clean-up preferences the edit follows too.
    private func editSelection(record: inout DictationRecord, pipeline: TranscriptionPipeline, generation: Int,
                               dictation: DictationSettings) async {
        let begun = ProcessInfo.processInfo.systemUptime
        // Read by ⌘C, the selection is only being read now that the keys are up.
        let reading = await selectionRead?.value
        // Esc (or a new dictation) meanwhile: `cancel` has dealt with this one.
        guard isCurrent(generation) else { return }
        guard let reading, case let .selection(selection, source) = reading.read, let place = reading.place else {
            let reason: SelectionRefusal
            if case let .refused(refusal) = reading?.read { reason = refusal } else { reason = .noSelection }
            note("command mode: selection refused (\(reason)) after recording")
            record.status = .polishFailed
            record.error = reason.message
            account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes())
            history.update(record)
            finish(showing: .error(reason.message), hideAfter: 3)
            return
        }
        record.selectionLength = selection.count
        // Saved while the edit runs, so the instruction survives a crash.
        history.update(record)

        let polished: SegmentedPolishResult
        do {
            polished = try await edit(selection, by: record.rawText, instructions: dictation.extraInstructions,
                                      onPartial: onPolishPartial(expected: selection))
        } catch {
            // Esc: `cancel` has kept the record as cancelled.
            if APIError.from(error) == .cancelled || !isCurrent(generation) { return }
            record.status = .polishFailed
            record.error = L("未能编辑：", "Couldn't edit: ") + error.localizedDescription
            account(&record, pipeline: pipeline, polished: nil, discarded: takeDiscardedPolishes())
            history.update(record)
            finish(showing: .error(L("未能编辑选中的文字：", "Couldn't edit the selection: ") + error.localizedDescription),
                   hideAfter: 5)
            return
        }
        noteTiming(of: polished, ahead: 0, in: &record, what: "edit of a selection")
        if isCurrent(generation) {
            hud.model.bar.reach(.delivered)
            let transcription = record.timing?.transcription ?? 0
            record.timing?.delivered = transcription + ProcessInfo.processInfo.systemUptime - begun
            if let fallback = polished.fallbacks.first?.fallback {
                note("edit unusable (\(fallback.rawValue)) → nothing pasted")
                record.status = .polishFailed
                record.error = L("未能编辑：\(fallback.commandReason)", "Couldn't edit: \(fallback.commandReason)")
                finish(showing: .error(L("未能编辑：\(fallback.commandReason)，选中的文字没有改动",
                                         "Couldn't edit: \(fallback.commandReason). Your text is unchanged")), hideAfter: 4)
            } else {
                record.status = .done
                // Neither is kept in History: both come from the selection (see `SelectionCommand.recordMode`).
                switch SelectionCommand.reply(polished.text, selection: selection) {
                case let .answer(answer):
                    TextInserter.copyToClipboard(answer)
                    record.timing?.delivery = "answered"
                    finish(showing: .answer(answer), hideAfter: 4)
                case let .rewrite(rewrite):
                    if SelectionCommand.isUnchanged(selection: selection, rewrite: rewrite) {
                        record.timing?.delivery = "unchanged"
                        finish(showing: .error(L("没有需要修改的地方", "Nothing to change")), hideAfter: 2.5)
                    } else {
                        record.timing?.delivery = await deliverEdit(
                            SelectionCommand.replacement(for: selection, rewrite: rewrite), at: place, original: selection,
                            source: source, mark: reading.mark, generation: generation)
                    }
                }
            }
        }
        // Esc came before the text was in place (or while it went in): `cancel` has kept the record as cancelled, and
        // so it stays, now with its edit.
        if Task.isCancelled { record.status = .cancelled }
        if record.status == .polishFailed { record.polishedText = nil }
        account(&record, pipeline: pipeline, polished: polished, discarded: takeDiscardedPolishes())
        history.update(record)
    }

    /// Pastes an edit over the selection it was made from, which the app's own undo then takes back. Only while it can
    /// go over that very selection (see `SelectionCommand.delivery`: the same app, window, tab and element in front, the
    /// same text still selected where Accessibility can tell, or else no click or key since it was read (`mark`),
    /// something that takes text, the screen unlocked); the app, the clicks and keys and the lock are checked once more
    /// right before ⌘V. Otherwise it goes to the clipboard, and the capsule says why. Returns how it went, for History.
    private func deliverEdit(_ text: String, at place: SelectedText.Place, original: String, source: SelectionSource,
                             mark: SelectedText.InputMark, generation: Int) async -> String {
        let canPaste = deliverByPaste && !isLocked
        let sameWindow = canPaste ? await place.isInFront() : false
        let selectionNow = sameWindow && source == .accessibility ? await SelectedText.selection(at: place) : nil
        let delivery = SelectionCommand.delivery(
            canPaste: canPaste, sameWindow: sameWindow, original: original, source: source, selectionNow: selectionNow,
            inputSinceRead: SelectedText.InputMark.now() != mark,
            editable: !sameWindow || FocusProbe.focusedTarget() != .notEditable)
        guard isCurrent(generation) else { return "cancelled" }
        if case let .clipboard(reason) = delivery {
            note("edit → clipboard (\(reason))")
            TextInserter.copyToClipboard(text)
            finish(showing: .error(reason.message), hideAfter: 3)
            return "copied"
        }
        let outcome = await TextInserter.insert(text, restoreClipboard: settings.restoreClipboard) {
            self.isCurrent(generation) && self.deliverByPaste && !self.isLocked
                && NSWorkspace.shared.frontmostApplication?.processIdentifier == place.pid
                && SelectedText.InputMark.now() == mark
        }
        guard isCurrent(generation) else {
            note("edit → \(outcome), but it was cancelled meanwhile")
            return "cancelled"
        }
        note("edit → \(outcome) in \(frontmostID)")
        switch outcome {
        case .pasted:
            reset()
            hud.finishWorking()
            return "pasted"
        case .notPasted:
            finish(showing: .copied, hideAfter: 1.6)
            return "failed"
        case .skipped:
            // The app, a click or key, or the lock changed just as ⌘V was due.
            TextInserter.copyToClipboard(text)
            let reason: SelectionCommand.ClipboardReason = !deliverByPaste || isLocked ? .locked
                : SelectedText.InputMark.now() != mark ? .selectionChanged : .movedAway
            finish(showing: .error(reason.message), hideAfter: 3)
            return "copied"
        case .noPermission:
            finish(showing: .error(L("没有辅助功能权限，文字已复制到剪贴板",
                                     "No Accessibility permission — text copied to the clipboard")), hideAfter: 3)
            return "failed"
        }
    }

    /// The edit of `selection` by `instruction`, with the clean-up's models and routes, and the speaker's clean-up
    /// preferences `instructions`.
    private func edit(_ selection: String, by instruction: String, instructions: String,
                      onPartial: (@Sendable (String) -> Void)? = nil) async throws -> SegmentedPolishResult {
        guard let endpoint = settings.polishEndpoint, settings.isConfigured(settings.polishProvider) else {
            throw APIError.missingAPIKey(settings.polishProvider.displayName)
        }
        let primary = route(endpoint, model: settings.polishModel, instructions: instructions, translation: nil,
                            selection: selection)
        let backup = settings.polishBackupEndpoint.map {
            route($0, model: settings.polishBackupModel, instructions: instructions, translation: nil, selection: selection)
        }
        return try await SelectionCommand.run(instruction: instruction, primary: primary, backup: backup,
                                              onPartial: onPartial)
    }

    /// Saves how long the clean-up (or translation) `outcome` took after the key was released (one started ahead of
    /// time had a head start of `ahead` seconds), and which model answered.
    private func noteTiming(of outcome: SegmentedPolishResult, ahead: Double, in record: inout DictationRecord, what: String) {
        let firstToken = max(0, outcome.firstTokenSeconds - ahead)
        let total = max(0, outcome.totalSeconds - ahead)
        record.timing?.polishFirstToken = firstToken
        record.timing?.polish = total
        record.timing?.polishModel = outcome.model
        record.timing?.usedBackup = outcome.usedBackup
        let fallbacks = outcome.fallbacks
        note(String(format: "%@: %@%@, %d part(s), first token %.2fs, total %.2fs", what, outcome.model,
                    outcome.usedBackup ? " (backup)" : "", outcome.parts.count, firstToken, total)
             + (fallbacks.isEmpty ? "" : "; unusable: " + fallbacks.map { $0.fallback?.rawValue ?? "" }.joined(separator: ", ")))
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

    /// Whether the app in front gets the settings `dictation` runs with: the same app profile as the app it was started
    /// in, or like it none (see `DictationSettings.suit`). Without any profiles, always.
    private func suits(_ dictation: DictationSettings) -> Bool {
        dictation.suit(NSWorkspace.shared.frontmostApplication?.bundleIdentifier, in: settings.appProfiles)
    }

    // MARK: - Clean-up ahead of time

    /// A clean-up of `raw` (or its translation into `translation`), started while recording, with the text around the
    /// cursor that was in by then.
    private struct SpeculativePolish {
        let raw: String
        let translation: TranslationTarget?
        let context: String?
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
    /// has one (they went on). Starts cleaning that transcript up (or translating it, in translation mode), so the text
    /// can be ready when the key is released.
    private func speculationChanged(_ raw: String?) {
        guard state == .recording, mode != .command, let dictation = dictationSettings,
              dictation.polishEnabled || mode == .translate else { return }
        if let raw, raw == speculativePolish?.raw { return }
        dropSpeculativePolish()
        guard let raw, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        // Only a transcript short enough for one request: a long one would go out as several at every pause, and its
        // parts are cleaned up in parallel once the key is released anyway.
        guard !SegmentedPolish.needsParts(raw) else { return }
        let outcome = PolishOutcome()
        let partials = PartialRelay()
        let translation = record?.translation
        let context = cursorContext
        let sent = contextSent
        let task = Task { [weak self] () async throws -> SegmentedPolishResult in
            guard let self else { throw APIError.cancelled }
            do {
                let result = try await self.polish(raw, with: dictation, translation: translation, context: context,
                                                   sent: sent, onPartial: { partials.send($0) })
                outcome.result = result
                return result
            } catch {
                outcome.failed = true
                throw error
            }
        }
        speculativePolish = SpeculativePolish(raw: raw, translation: translation, context: context,
                                              startedAt: ProcessInfo.processInfo.systemUptime, task: task,
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
    /// stopped, or the app in front has other settings than the dictation's (see `suits`). Whether the paste actually
    /// landed is detected from the target app reading the clipboard: if it did, the user's previous clipboard is
    /// restored; if not, the text stays on the clipboard and the HUD says so. Returns how it went, for History (see
    /// `DictationRecord.Timing.delivery`). A paste is fitted to the text around the cursor first (see `fitToCursor`);
    /// what's left on the clipboard is the text as cleaned up. Return follows the paste when the dictation's settings
    /// (its app profile's, or the global one) say so.
    ///
    /// Esc (or the screen locking) can come at any point while the text goes in: the paste and the Return are only sent
    /// while the dictation is still current, and a cancelled one leaves the session and the HUD to what replaced it.
    ///
    /// `watchEdits`: false for a translation, whose edits say nothing about what speech recognition misheard.
    @discardableResult
    private func deliver(_ text: String, notice: String?, generation: Int, dictation: DictationSettings,
                         watchEdits: Bool = true) async -> String {
        let target = deliverByPaste ? FocusProbe.focusedTarget() : .notEditable
        let otherSettings = deliverByPaste && !suits(dictation)
        if target == .notEditable || deliverByPaste && focusMoved || otherSettings {
            let reason = target == .notEditable ? "focus not editable" : otherSettings ? "an app with other settings in front" : "another app in front"
            note("deliver → clipboard (\(reason)) in \(frontmostID)")
            TextInserter.copyToClipboard(text)
            finish(showing: notice.map { .error($0) } ?? .copied, hideAfter: 1.6)
            return "copied"
        }
        // The fitted text goes in only while the cursor is still where it was read; otherwise the text as cleaned up does.
        let fit = fitToCursor(text)
        var fits = fit != nil
        let outcome = await TextInserter.insert(
            fit?.text ?? text, fallback: text, restoreClipboard: settings.restoreClipboard,
            shouldPaste: { self.isCurrent(generation) && self.deliverByPaste && !self.focusMoved && self.suits(dictation) },
            stillFits: {
                guard let fit else { return true }
                fits = FocusProbe.isCursorStill(at: fit.surroundings)
                if !fits { self.note("fitted text not used: the cursor moved, or couldn't be checked") }
                return fits
            })
        let inserted = fits ? fit?.text ?? text : text
        guard isCurrent(generation) else {
            note("deliver → \(outcome), but the dictation was cancelled meanwhile")
            return "cancelled"
        }
        note("deliver → \(outcome) (focus \(target)) in \(frontmostID)")
        switch outcome {
        case .pasted:
            // Sends it, in a chat box or an AI prompt. Then there's no text left to watch for corrections.
            var sent = false
            if dictation.pressEnter {
                // Not into another app that came to the front after the paste, nor once the screen locked.
                sent = await TextInserter.pressReturn {
                    self.isCurrent(generation) && self.deliverByPaste && !self.focusMoved && self.suits(dictation)
                }
                guard isCurrent(generation) else { return "pasted" }
            }
            if let notice {
                finish(showing: .error(notice), hideAfter: 3)
            } else {
                reset()
                hud.finishWorking()
            }
            if settings.learnFromEdits, watchEdits, !sent { editWatcher.watch(inserted: inserted) }
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

    /// The text with its spacing and capitalisation fitted to the text around the cursor (see InsertionJoiner), and where
    /// that was read; nil when fitting is off, the field can't be read, or nothing needs changing. History keeps the
    /// cleaned text: this is only how it goes in.
    private func fitToCursor(_ text: String) -> (text: String, surroundings: FocusProbe.Surroundings)? {
        guard settings.fitToSurroundingText, let surroundings = FocusProbe.textAroundCursor() else { return nil }
        let result = InsertionJoiner.fit(text, before: surroundings.before, after: surroundings.after,
                                         vocabulary: settings.vocabularyList, isCommonWord: EditWatcher.isCommonWord)
        guard !result.changes.isEmpty else { return nil }
        note("fitted to the text around the cursor: \(result.changes.joined(separator: ", "))")
        return (result.text, surroundings)
    }

    /// Cleans up `raw` with the dictation's preferences (the global ones and its app profile's; nil: the global ones), or
    /// translates it into `translation`, on the main model and the backup alike. `context`: the text around the cursor,
    /// which goes with every clean-up request (both routes, every part); `sent` is told when one of them goes out, for
    /// the dictation the clean-up is for.
    ///
    /// `forFile`: the clean-up of a file being transcribed (see FileTranscriber), which sends a few parts at a time, on
    /// the file session, each part waiting to start while `holdWhile` says so (a dictation is under way). A dictation's
    /// own clean-up sends all of its parts at once.
    func polish(_ raw: String, with dictation: DictationSettings? = nil, translation: TranslationTarget? = nil,
                context: String? = nil, sent: ContextSent? = nil, forFile: Bool = false,
                holdWhile: (@Sendable () -> Bool)? = nil,
                onPartial: (@Sendable (String) -> Void)? = nil) async throws -> SegmentedPolishResult {
        guard let endpoint = settings.polishEndpoint, settings.isConfigured(settings.polishProvider) else {
            throw APIError.missingAPIKey(settings.polishProvider.displayName)
        }
        let instructions = (dictation ?? settings.globalDictationSettings).extraInstructions
        let primary = route(endpoint, model: settings.polishModel, instructions: instructions, translation: translation,
                            context: context, sent: sent, forFile: forFile)
        let backup = settings.polishBackupEndpoint.map {
            route($0, model: settings.polishBackupModel, instructions: instructions, translation: translation,
                  context: context, sent: sent, forFile: forFile)
        }
        return try await SegmentedPolish.run(transcript: raw, primary: primary, backup: backup,
                                             concurrency: forFile ? FileTranscription.cleanUpConcurrency : nil,
                                             holdWhile: holdWhile, onPartial: onPartial)
    }

    private func route(_ endpoint: ProviderEndpoint, model: String, instructions: String, translation: TranslationTarget?,
                       selection: String? = nil, context: String? = nil, sent: ContextSent? = nil,
                       forFile: Bool = false) -> PolishRoute {
        let model = model.trimmingCharacters(in: .whitespaces)
        return PolishRoute(client: forFile ? .forFiles(endpoint: endpoint) : APIClient(endpoint: endpoint), options: PolishOptions(
            model: model,
            vocabulary: settings.dictationVocabulary,
            misheard: settings.misheardHints,
            extraInstructions: instructions,
            modelInfo: endpoint.id == .openrouter ? modelInfo[model] : nil,
            host: endpoint.id == .openrouter ? settings.openRouterHost(for: model) : nil,
            translation: translation,
            selection: selection,
            context: context,
            onContextSent: { sent?.mark() }
        ))
    }

    /// Prices what this run's requests used (the speech-to-text of every chunk sent, and every clean-up answer, used or
    /// not, with the hedge's cancelled requests) and adds it, with the dictation's words once it's finished, to the Home
    /// page totals. `discarded`: clean-ups done ahead of time for a transcript the speaker then went on from.
    /// `context`: whether this run's requests carried the text around the cursor, which History notes (never the text);
    /// nil for a file's, which never send it.
    func account(_ record: inout DictationRecord, pipeline: TranscriptionPipeline, polished: SegmentedPolishResult?,
                 discarded: [SegmentedPolishResult], context: ContextSent? = nil) {
        if let context { noteContext(&record, context) }
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

    /// Notes in History that a clean-up request of this run carried the text around the cursor; the text isn't kept.
    private func noteContext(_ record: inout DictationRecord, _ sent: ContextSent) {
        guard sent.isSet else { return }
        if record.timing == nil { record.timing = DictationRecord.Timing() }
        record.timing?.usedContext = true
    }

    // MARK: - Action shortcuts

    /// An action's shortcut was pressed (see `ShortcutAction`). Only between dictations: while one is under way its keys
    /// do nothing (they're kept from the focused app all the same). `shortcut`: the keys that were pressed.
    func perform(_ action: ShortcutAction, shortcut: Hotkey?) {
        guard state == .idle else {
            note("\(action.rawValue) ignored — a dictation is under way")
            return
        }
        switch action {
        case .pasteLastDictation: pasteLastDictation(releasing: shortcut)
        }
    }

    /// Puts the newest finished dictation at the cursor again, the way a dictation's text goes in: pasted, with the
    /// clipboard put back afterwards, or left on the clipboard when it can't be. When the newest one failed with its
    /// recording saved, re-sends that instead and pastes the result (see `LastDictation`).
    ///
    /// The text goes to the app in front when it was asked for (from the menu-bar panel: the app in front before the
    /// panel was clicked), and only while that app is still in front; otherwise to the clipboard.
    ///
    /// `shortcut`: the keys that asked for it, let go of before anything is pasted, since a paste read together with
    /// keys still held would be another shortcut altogether. nil from the menu bar, whose panel first has to give focus
    /// back to the app the text goes to.
    func pasteLastDictation(releasing shortcut: Hotkey?) {
        guard state == .idle, pendingPasteLast == nil else { return }
        let front = NSWorkspace.shared.frontmostApplication
        let target = shortcut == nil ? Self.otherApp(front) ?? lastOtherApp : front?.processIdentifier
        pasteLastCount += 1
        let id = pasteLastCount
        pendingPasteLast = id
        Task { [weak self] in
            let released: Bool
            if let shortcut {
                released = await HotkeyMonitor.waitForRelease(of: shortcut)
            } else {
                try? await Task.sleep(nanoseconds: 300_000_000)
                released = true
            }
            await self?.pasteLast(id, into: target, released: released)
        }
    }

    /// `released`: false when the shortcut's keys were still held after a while; the text then goes to the clipboard.
    private func pasteLast(_ id: Int, into target: pid_t?, released: Bool) async {
        defer { if pendingPasteLast == id { pendingPasteLast = nil } }
        // Dropped meanwhile: by Esc, the screen locking, or a dictation started while the keys were still held.
        guard pasteLastIsCurrent(id) else { return }
        let records = history.records
        let entries = records.map {
            LastDictation.Entry(kind: $0.status.pruningKind,
                                hasText: !$0.finalText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                                // Only a failed one is ever re-sent, so only its recording is looked for on disk.
                                hasAudio: $0.status == .failed && $0.hasAudio,
                                isCommand: $0.isCommand)
        }
        switch LastDictation.choose(entries) {
        case .nothing:
            note("paste last dictation → nothing to paste")
            hud.show(.error(L("还没有可以粘贴的听写", "No dictation to paste yet")), autoHideAfter: 2)
        case let .retry(index):
            note("paste last dictation → the newest one failed: retried")
            // Its text goes in like a dictation's: only into `target`, while it's still in front (see `focusMoved`).
            retry(records[index], paste: released && !isLocked, target: target)
        case let .paste(index):
            let text = records[index].finalText
            // The keys still held, another app in front, or focus on something that can't take text: the clipboard.
            guard released, isInFront(target), FocusProbe.focusedTarget() != .notEditable else {
                note("paste last dictation → clipboard")
                TextInserter.copyToClipboard(text)
                hud.show(.copied, autoHideAfter: 1.6)
                return
            }
            let outcome = await TextInserter.insert(text, restoreClipboard: settings.restoreClipboard) {
                self.pasteLastIsCurrent(id) && self.isInFront(target)
            }
            note("paste last dictation → \(outcome) in \(frontmostID)")
            // Esc, the screen locking or a new dictation while it went in: that leaves the HUD and the clipboard alone.
            guard pasteLastIsCurrent(id) else { return }
            switch outcome {
            case .pasted:
                break
            case .notPasted:
                hud.show(.copied, autoHideAfter: 1.6)
            case .skipped:
                // Another app came to the front just as ⌘V was due.
                TextInserter.copyToClipboard(text)
                hud.show(.copied, autoHideAfter: 1.6)
            case .noPermission:
                hud.show(.error(L("没有辅助功能权限，文字已复制到剪贴板",
                                  "No Accessibility permission — text copied to the clipboard")), autoHideAfter: 3)
            }
        }
    }

    /// Whether the "Paste last dictation" `id` is still wanted: not dropped, and no dictation under way.
    private func pasteLastIsCurrent(_ id: Int) -> Bool {
        pendingPasteLast == id && state == .idle && !isLocked
    }

    private func dropPendingPasteLast(_ reason: String) {
        guard pendingPasteLast != nil else { return }
        pendingPasteLast = nil
        note("paste last dictation dropped — \(reason)")
    }

    /// Whether `app` is in front (nil: the app couldn't be told when it was asked for, so there's nothing to check).
    private func isInFront(_ app: pid_t?) -> Bool {
        app == nil || NSWorkspace.shared.frontmostApplication?.processIdentifier == app
    }

    /// `app`, unless it's this one.
    private static func otherApp(_ app: NSRunningApplication?) -> pid_t? {
        guard let app, app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return nil }
        return app.processIdentifier
    }

    // MARK: - Retry / cancel

    /// Re-runs a saved dictation. Chunks that already succeeded are reused; only failed ones are re-sent. `target`: the
    /// app its text is pasted into, only while that app is in front (nil: whichever is in front then).
    func retry(_ saved: DictationRecord, paste: Bool, target: pid_t? = nil) {
        guard state == .idle else { return }
        // An edit of a selection can't run again without its selection, which isn't kept; run as a dictation, it would
        // paste the instruction.
        guard !saved.isCommand else {
            hud.show(.error(L("编辑选中文字的记录不能重试", "An edit of a selection can't be retried")), autoHideAfter: 3)
            return
        }
        guard let endpoint = settings.sttEndpoint else { return }
        guard saved.hasAudio, let data = try? Data(contentsOf: saved.audioURL),
              let samples = WAV.decodeSamples(data) else {
            hud.show(.error(L("找不到这条记录的录音文件", "The recording for this entry is gone")), autoHideAfter: 3)
            return
        }
        deliverByPaste = paste
        // A cancelled recording still being transcribed in the background is this run's now.
        keptInBackground.remove(saved.id)
        // The app profile it was made with, while that still exists; the global settings otherwise.
        let dictation = settings.dictationSettings(profileID: saved.profileID)
        var record = saved
        record.status = .processing
        record.error = nil
        record.profileID = dictation.profileID
        record.profileName = dictation.profileName
        // Saved transcripts are reused by chunk index, so only when the recording is cut into the same chunks again, and
        // transcribed with the same language hint (a profile, or the global setting, may have changed it since).
        let preset = dictation.reusesTranscripts(layout: saved.chunkLayout, language: saved.chunkLanguage) ? saved.chunkTexts : [:]
        // Saved with the new layout and language, so only transcripts of the new chunks may stay (a quit mid-retry keeps them).
        record.chunkTexts = preset
        record.chunkLayout = Chunker.layout
        record.chunkLanguage = dictation.language
        history.update(record)
        self.record = record
        dictationSettings = dictation
        pasteTarget = target
        state = .processing
        // A translation dictation is translated again, into the language it was translated into then.
        mode = record.translation == nil ? .dictation : .translate
        hud.model.badge = record.translation?.badge
        hud.showWorking(polishes: dictation.polishEnabled || record.translation != nil)

        let pipeline = makePipeline(endpoint: endpoint, preset: preset, language: dictation.language)
        self.pipeline = pipeline
        let generation = self.generation
        processingTask = Task { [weak self] in
            // Let the menu close and focus return to the previous app before pasting.
            try? await Task.sleep(nanoseconds: 300_000_000)
            pipeline.append(samples)
            await self?.process(record: record, pipeline: pipeline, generation: generation, dictation: dictation)
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
            noteContext(&record, contextSent)
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
        dictationSettings = nil
        pressedAt = nil
        pasteTarget = nil
        handsFree = false
        mode = .dictation
        dropSelection()
        processingTask = nil
        stopCursorContextRead()
        cursorContext = nil
        contextSent = ContextSent()
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
