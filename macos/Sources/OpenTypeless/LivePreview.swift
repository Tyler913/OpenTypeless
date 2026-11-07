import AVFoundation
import os
import Speech
import TypelessCore

/// Shows what's being said above the recording capsule while the user talks, recognised on this Mac by
/// SpeechAnalyzer from the same audio that's being recorded. Only a preview: nothing it produces is inserted or
/// saved; the dictation's text still comes from the speech-to-text provider.
///
/// Thread-safe: `append` runs on the audio thread, `start` / `stop` on the main thread.
final class LivePreview: @unchecked Sendable {
    private let lock = NSLock()
    private let onText: @Sendable (String) -> Void
    private let log = Logger(subsystem: "local.opentypeless.app", category: "preview")
    private let sourceFormat = AppleSpeechRecognizer.sourceFormat
    private var continuation: AsyncStream<AnalyzerInput>.Continuation?
    private var converter: AVAudioConverter?
    private var analyzerFormat: AVAudioFormat?
    private var analyzer: SpeechAnalyzer?
    private var task: Task<Void, Never>?
    private var stopped = false

    /// `onText` gets everything recognised so far, on a background thread.
    init(onText: @escaping @Sendable (String) -> Void) {
        self.onText = onText
    }

    /// Starts recognising `language` (an ISO code such as "zh", or "" for the system language). Setting up takes a
    /// moment, and audio from before that isn't previewed.
    func start(language: String) {
        task = Task { [weak self] in await self?.run(language: language) }
    }

    func stop() {
        let (continuation, analyzer) = lock.withLock { () -> (AsyncStream<AnalyzerInput>.Continuation?, SpeechAnalyzer?) in
            stopped = true
            defer { self.continuation = nil; self.analyzer = nil }
            return (self.continuation, self.analyzer)
        }
        continuation?.finish()
        task?.cancel()
        if let analyzer { Task { try? await analyzer.finalizeAndFinishThroughEndOfInput() } }
    }

    /// 16 kHz mono samples of the dictation, in order.
    func append(_ samples: [Int16]) {
        guard !samples.isEmpty else { return }
        let state = lock.withLock { () -> (AVAudioConverter, AVAudioFormat, AsyncStream<AnalyzerInput>.Continuation)? in
            guard let c = self.converter, let f = self.analyzerFormat, let k = self.continuation else { return nil }
            return (c, f, k)
        }
        guard let state else { return }
        let (audioConverter, format, stream) = state
        guard let output = AppleSpeechRecognizer.convert(samples, with: audioConverter, to: format, endOfStream: false) else {
            return
        }
        stream.yield(AnalyzerInput(buffer: output))
    }

    private func run(language: String) async {
        // The same locale on-device speech-to-text would pick (see OnDeviceLocale).
        guard let locale = await OnDeviceModels.locale(for: language) else {
            log.debug("no on-device speech model for \(language, privacy: .public)")
            return
        }
        guard await OnDeviceModels.isInstalled(locale) else {
            // First use of this language: fetch the model in the background; the preview starts next time.
            await OnDeviceModels.shared.install(locale)
            return
        }
        let transcriber = SpeechTranscriber(locale: Locale(identifier: locale), transcriptionOptions: [],
                                            reportingOptions: [.volatileResults], attributeOptions: [])
        do {
            guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]),
                  let converter = AVAudioConverter(from: sourceFormat, to: format) else { return }
            let analyzer = SpeechAnalyzer(modules: [transcriber])
            let (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream()
            let ready = lock.withLock { () -> Bool in
                guard !stopped else { return false }
                self.converter = converter
                self.analyzerFormat = format
                self.continuation = continuation
                self.analyzer = analyzer
                return true
            }
            guard ready else { return }
            try await analyzer.start(inputSequence: stream)
            var finished = ""
            for try await result in transcriber.results {
                let text = String(result.text.characters)
                if result.isFinal {
                    finished += text
                    onText(finished)
                } else {
                    onText(finished + text)
                }
            }
        } catch {
            log.debug("live preview stopped: \(error.localizedDescription, privacy: .public)")
        }
    }
}
