import AVFoundation
import Foundation
import TypelessCore

enum CLI {
    static func transcribe(path: String, polish: Bool, realtime: Bool) async -> Int32 {
        let settings = AppSettings.shared
        let url = URL(fileURLWithPath: path)
        let samples: [Int16]
        do {
            samples = try loadAudio(url)
        } catch {
            log("❌ Can't read audio: \(error.localizedDescription)")
            return 1
        }
        let duration = AudioFormat.seconds(forSampleCount: samples.count)
        if CommandLine.arguments.contains("--chunks-only") {
            printChunks(samples)
            return 0
        }
        guard let sttEndpoint = settings.sttEndpoint else {
            log("❌ The speech-to-text provider's base URL is invalid")
            return 1
        }
        let client = APIClient(endpoint: sttEndpoint)
        log("🎧 \(url.lastPathComponent): \(String(format: "%.1f", duration)) s")
        log("   STT: \(settings.sttProvider.displayName) / \(settings.sttModel)"
            + (polish ? "; clean-up: \(settings.polishProvider.displayName) / \(settings.polishModel)" : ""))

        let start = Date()
        let pipeline = TranscriptionPipeline(
            client: client,
            options: .init(model: settings.sttModel, language: settings.sttLanguage.isEmpty ? nil : settings.sttLanguage),
            observer: { index, state in
                let t = String(format: "%5.1fs", Date().timeIntervalSince(start))
                switch state {
                case let .transcribing(attempt) where attempt > 1: log("[\(t)] chunk \(index): attempt \(attempt)")
                case let .done(text): log("[\(t)] chunk \(index) ✅ \(text.prefix(40))…")
                case let .failed(message): log("[\(t)] chunk \(index) ❌ \(message)")
                case .skippedSilence: log("[\(t)] chunk \(index) silent, skipped")
                default: break
                }
            }
        )

        if realtime {
            // Feed audio at real speed, like a live microphone, to exercise the streaming path.
            let block = AudioFormat.sampleCount(forSeconds: 0.1)
            var i = 0
            while i < samples.count {
                pipeline.append(Array(samples[i..<min(i + block, samples.count)]))
                i += block
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        } else {
            pipeline.append(samples)
        }
        let released = Date()

        let raw: String
        do {
            raw = try await pipeline.finish()
        } catch {
            log("❌ Transcription failed: \(error.localizedDescription)")
            return 2
        }
        log(String(format: "⏱  Transcribed: %.1f s after release (%.1f s total)",
                   Date().timeIntervalSince(released), Date().timeIntervalSince(start)))
        print("\n===== Raw transcript (\(raw.count) chars) =====\n\(raw)\n")

        guard polish else { return 0 }
        guard let polishEndpoint = settings.polishEndpoint else {
            log("❌ The clean-up provider's base URL is invalid")
            return 3
        }
        let polishStart = Date()
        do {
            let usesOpenRouter = polishEndpoint.id == .openrouter || settings.polishBackupEndpoint?.id == .openrouter
            var models: [APIClient.ModelInfo]?
            if usesOpenRouter, let endpoint = settings.endpoint(for: .openrouter) {
                models = try? await APIClient(endpoint: endpoint).listModels()
            }
            func route(_ endpoint: ProviderEndpoint, _ model: String) -> PolishRoute {
                PolishRoute(client: APIClient(endpoint: endpoint), options: PolishOptions(
                    model: model, vocabulary: settings.vocabularyList, misheard: settings.misheardHints,
                    extraInstructions: settings.extraInstructions,
                    modelInfo: endpoint.id == .openrouter ? models?.first { $0.id == model } : nil))
            }
            let outcome = try await HedgedPolish.run(
                transcript: raw,
                primary: route(polishEndpoint, settings.polishModel),
                backup: settings.polishBackupEndpoint.map { route($0, settings.polishBackupModel) })
            let result = outcome.result
            log(String(format: "⏱  Clean-up took %.1f s (first token %.2f s, %@%@)%@", Date().timeIntervalSince(polishStart),
                       outcome.firstTokenSeconds, outcome.usedBackup ? "backup " : "", outcome.model,
                       result.truncated ? " (truncated)" : ""))
            if Prompts.looksLikeAnAnswer(input: raw, output: result.text) {
                log("⚠️ Output is much longer than the input; the app would fall back to the raw transcript")
            }
            print("===== Cleaned up (\(result.text.count) chars) =====\n\(result.text)\n")
            return 0
        } catch {
            log("❌ Clean-up failed: \(error.localizedDescription)")
            return 3
        }
    }

    /// Shows where the chunker cuts and how quiet each cut point is (dBFS over ±100 ms).
    static func printChunks(_ samples: [Int16]) {
        let chunker = Chunker()
        var chunks = chunker.append(samples)
        if let tail = chunker.finish() { chunks.append(tail) }
        let radius = AudioFormat.sampleCount(forSeconds: 0.1)
        let speechRMS = AudioLevel.rms(samples)
        print(String(format: "Duration %.1f s, overall RMS %.1f dBFS, %d chunks", AudioFormat.seconds(forSampleCount: samples.count),
                     20 * log10(max(speechRMS, 1e-6)), chunks.count))
        for chunk in chunks {
            let end = chunk.startSample + chunk.samples.count
            let lo = max(0, end - radius), hi = min(samples.count, end + radius)
            let cutRMS = AudioLevel.rms(samples[lo..<hi])
            print(String(format: "  chunk %d: %6.2fs → %6.2fs (%.1fs), cut-point energy %.1f dBFS%@", chunk.index,
                         chunk.startTime, chunk.startTime + chunk.duration, chunk.duration,
                         20 * log10(max(cutRMS, 1e-6)), AudioLevel.isSilent(chunk.samples) ? " (silent)" : ""))
        }
    }

    /// Reads any audio file AVFoundation understands and converts it to 16 kHz mono Int16.
    static func loadAudio(_ url: URL) throws -> [Int16] {
        let file = try AVAudioFile(forReading: url)
        let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: Double(AudioFormat.sampleRate),
                                   channels: 1, interleaved: true)!
        guard let converter = AVAudioConverter(from: file.processingFormat, to: target) else {
            throw NSError(domain: "CLI", code: 1, userInfo: [NSLocalizedDescriptionKey: "unsupported format"])
        }
        var result: [Int16] = []
        let inputBuffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 8192)!
        var reachedEnd = false
        while true {
            let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: 8192)!
            var error: NSError?
            let status = converter.convert(to: output, error: &error) { _, outStatus in
                if reachedEnd { outStatus.pointee = .endOfStream; return nil }
                do {
                    try file.read(into: inputBuffer)
                } catch {
                    reachedEnd = true
                    outStatus.pointee = .endOfStream
                    return nil
                }
                if inputBuffer.frameLength == 0 {
                    reachedEnd = true
                    outStatus.pointee = .endOfStream
                    return nil
                }
                outStatus.pointee = .haveData
                return inputBuffer
            }
            if let error { throw error }
            if output.frameLength > 0, let channel = output.int16ChannelData {
                result.append(contentsOf: UnsafeBufferPointer(start: channel[0], count: Int(output.frameLength)))
            }
            if status == .endOfStream || status == .error { break }
        }
        return result
    }

    static func log(_ message: String) {
        FileHandle.standardError.write(Data((message + "\n").utf8))
    }
}
