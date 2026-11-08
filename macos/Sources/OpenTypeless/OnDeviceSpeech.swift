import AVFoundation
import Combine
import Network
import os
import Speech
import TypelessCore

/// Whether this Mac has a network path at all (`NWPathMonitor`), so a dictation goes to the recognizer on this Mac at
/// once while it hasn't, rather than waiting on requests that can't get out (see `OfflineFallback`). Watched from the
/// first use (the app's start); until the system's first answer the network counts as up.
final class NetworkStatus: @unchecked Sendable {
    static let shared = NetworkStatus()

    private let monitor = NWPathMonitor()
    private let lock = NSLock()
    private var down = false

    private init() {
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            // `.requiresConnection` (a VPN on demand, say) may come up as the request goes out: not down.
            self.lock.withLock { self.down = path.status == .unsatisfied }
        }
        monitor.start(queue: DispatchQueue(label: "local.opentypeless.network-status"))
    }

    var isDown: Bool { lock.withLock { down } }
}

/// The speech models of the recognizer on this Mac: which language each dictation is recognised in, and downloading a
/// language's model the first time it's used (in the background, with its progress for Settings and the capsule). The
/// live preview and on-device speech-to-text share it, so a model is fetched once.
@MainActor
final class OnDeviceModels: ObservableObject {
    static let shared = OnDeviceModels()

    enum Status: Equatable {
        /// Being downloaded; how much of it is there (0…1).
        case downloading(Double)
        case ready
        case failed(String)
    }

    /// By locale (BCP-47, "en-US"); a locale not in here hasn't been looked at yet.
    @Published private(set) var statuses: [String: Status] = [:]
    private var installs: [String: Task<Void, Never>] = [:]
    private let log = Logger(subsystem: "local.opentypeless.app", category: "speech-models")

    /// The locale a spoken-language hint ("zh", or "" to auto-detect) is recognised in, or nil when there's no model for
    /// it (see `OnDeviceLocale`).
    nonisolated static func locale(for language: String) async -> String? {
        await OnDeviceRoute(recognizer: AppleSpeechRecognizer.shared).locale(for: language)
    }

    /// Starts fetching `locale`'s model unless it's there or on its way; returns at once.
    func install(_ locale: String) {
        if installs[locale] != nil { return }
        installs[locale] = Task { await self.run(locale) }
    }

    /// Returns once `locale`'s model is installed, fetching it first if need be. Throws when it can't be fetched, or the
    /// waiting task is cancelled (the download goes on).
    func waitUntilInstalled(_ locale: String) async throws {
        if case .ready = statuses[locale], await Self.isInstalled(locale) { return }
        switch statuses[locale] {
        case .failed?, .ready?: // failed before, or removed by the system since: fetched again
            statuses[locale] = nil
            installs[locale] = nil
        default: break
        }
        install(locale)
        while true {
            try Task.checkCancellation()
            switch statuses[locale] {
            case .ready?: return
            case let .failed(message)?: throw APIError.network(message)
            default: try await Task.sleep(nanoseconds: 200_000_000)
            }
        }
    }

    nonisolated static func isInstalled(_ locale: String) async -> Bool {
        await AssetInventory.status(forModules: [AppleSpeechRecognizer.transcriber(locale)]) == .installed
    }

    private func run(_ locale: String) async {
        let transcriber = AppleSpeechRecognizer.transcriber(locale)
        do {
            // Kept for this app, so the system doesn't take it back to free space while it's in use.
            _ = try? await AssetInventory.reserve(locale: Locale(identifier: locale))
            guard let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) else {
                statuses[locale] = .ready
                return
            }
            log.debug("downloading the speech model for \(locale, privacy: .public)")
            statuses[locale] = .downloading(0)
            let progress = request.progress
            let watch = Task { @MainActor [weak self] in
                while !Task.isCancelled {
                    if case .downloading? = self?.statuses[locale] {
                        self?.statuses[locale] = .downloading(min(1, max(0, progress.fractionCompleted)))
                    }
                    try? await Task.sleep(nanoseconds: 250_000_000)
                }
            }
            defer { watch.cancel() }
            try await request.downloadAndInstall()
            statuses[locale] = .ready
        } catch {
            log.debug("speech model for \(locale, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            statuses[locale] = .failed(error.localizedDescription)
        }
        installs[locale] = nil
    }
}

/// Apple's SpeechAnalyzer with a SpeechTranscriber, one chunk at a time: the speech-to-text on this Mac.
final class AppleSpeechRecognizer: OnDeviceRecognizer {
    static let shared = AppleSpeechRecognizer()
    /// Standing in for the provider while the network is down (see `OfflineFallback`): a language's model that isn't
    /// here can't be downloaded then, so the chunk fails at once instead.
    static let installedOnly = AppleSpeechRecognizer(downloadsModels: false)

    /// Whether a language's model is downloaded the first time it's needed.
    private let downloadsModels: Bool

    private init(downloadsModels: Bool = true) {
        self.downloadsModels = downloadsModels
    }

    static let sourceFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: Double(AudioFormat.sampleRate),
                                                    channels: 1, interleaved: true)!

    /// Final results only, with punctuation and capitals as typed text has them.
    static func transcriber(_ locale: String) -> SpeechTranscriber {
        SpeechTranscriber(locale: Locale(identifier: locale), preset: .transcription)
    }

    func supportedLocales() async -> [String] {
        await SpeechTranscriber.supportedLocales.map { $0.identifier(.bcp47) }
    }

    func transcribe(samples: [Int16], locale: String, vocabulary: [String]) async throws -> String {
        if downloadsModels {
            try await OnDeviceModels.shared.waitUntilInstalled(locale)
        } else if !(await OnDeviceModels.isInstalled(locale)) {
            throw APIError.unavailable(L("语音模型还没有下载", "The speech model isn't downloaded yet"))
        }
        let transcriber = Self.transcriber(locale)
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]),
              let converter = AVAudioConverter(from: Self.sourceFormat, to: format),
              let buffer = Self.convert(samples, with: converter, to: format, endOfStream: true) else {
            throw APIError.recognition(L("无法转换音频格式", "Couldn't convert the audio for the recognizer"))
        }
        // Kept loaded for a while after each chunk, so the next one doesn't load it again.
        let analyzer = SpeechAnalyzer(modules: [transcriber],
                                      options: SpeechAnalyzer.Options(priority: .userInitiated, modelRetention: .lingering))
        if !vocabulary.isEmpty {
            let context = AnalysisContext()
            context.contextualStrings[.general] = vocabulary
            try? await analyzer.setContext(context)
        }
        let results = Task { () -> [String] in
            var parts: [String] = []
            for try await result in transcriber.results { parts.append(String(result.text.characters)) }
            return parts
        }
        let (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream()
        continuation.yield(AnalyzerInput(buffer: buffer))
        continuation.finish()
        let seconds = AudioFormat.seconds(forSampleCount: samples.count)
        do {
            try await withTaskCancellationHandler {
                try await withTimeout(max(30, seconds * 2 + 15)) {
                    if let end = try await analyzer.analyzeSequence(stream) {
                        try await analyzer.finalizeAndFinish(through: end)
                    } else {
                        await analyzer.cancelAndFinishNow()
                    }
                }
            } onCancel: {
                Task { await analyzer.cancelAndFinishNow() }
            }
            return TranscriptJoiner.join(try await results.value)
        } catch {
            results.cancel()
            await analyzer.cancelAndFinishNow()
            if Task.isCancelled { throw APIError.cancelled }
            if error is APIError { throw error }
            throw APIError.recognition(L("设备端识别失败：", "On-device recognition failed: ") + error.localizedDescription)
        }
    }

    /// 16 kHz mono samples in the analyzer's format. `endOfStream`: these are all there is, so the converter hands over
    /// everything it holds; otherwise (a stream, as the live preview's) it keeps what the next samples follow on from.
    static func convert(_ samples: [Int16], with converter: AVAudioConverter, to format: AVAudioFormat,
                        endOfStream: Bool) -> AVAudioPCMBuffer? {
        guard !samples.isEmpty,
              let input = AVAudioPCMBuffer(pcmFormat: sourceFormat, frameCapacity: AVAudioFrameCount(samples.count)),
              let channel = input.int16ChannelData else { return nil }
        samples.withUnsafeBufferPointer { channel[0].update(from: $0.baseAddress!, count: samples.count) }
        input.frameLength = AVAudioFrameCount(samples.count)
        let capacity = AVAudioFrameCount(Double(samples.count) * format.sampleRate / sourceFormat.sampleRate) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return nil }
        var consumed = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if consumed {
                status.pointee = endOfStream ? .endOfStream : .noDataNow
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return input
        }
        guard error == nil, output.frameLength > 0 else { return nil }
        return output
    }
}
