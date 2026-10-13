import AVFoundation
import Foundation
import Testing
@testable import TypelessCore

@Suite struct FileTranscriptionTests {
    private struct Probe: Codable, Equatable {
        var source: RecordSource?
    }

    @Test func recordSourceRoundTripsAndOlderRecordsAreDictations() throws {
        let encoded = try JSONEncoder().encode(Probe(source: .file))
        #expect(String(decoding: encoded, as: UTF8.self) == #"{"source":"file"}"#)
        #expect(try JSONDecoder().decode(Probe.self, from: encoded) == Probe(source: .file))
        // Saved before files could be transcribed: no key at all, and nothing is written for a dictation.
        #expect(try JSONDecoder().decode(Probe.self, from: Data("{}".utf8)).source == nil)
        #expect(String(decoding: try JSONEncoder().encode(Probe(source: nil)), as: UTF8.self) == "{}")
        // A source a later version adds doesn't lose the record.
        #expect(try JSONDecoder().decode(Probe.self, from: Data(#"{"source":"screen"}"#.utf8)).source == .dictation)
    }

    @Test func costEstimateIsPerMinuteOfAudio() throws {
        let price = ModelPrice(perMinute: 0.006)
        #expect(abs(try #require(FileTranscription.estimatedCost(seconds: 3600, price: price)) - 0.36) < 1e-9)
        #expect(abs(try #require(FileTranscription.estimatedCost(seconds: 90, price: price)) - 0.009) < 1e-9)
        // Nothing to go on: no price, a model billed by the token, or a length that isn't known.
        #expect(FileTranscription.estimatedCost(seconds: 3600, price: nil) == nil)
        #expect(FileTranscription.estimatedCost(seconds: 3600, price: ModelPrice(inputPerMillion: 2.5, outputPerMillion: 10)) == nil)
        #expect(FileTranscription.estimatedCost(seconds: 0, price: price) == nil)
    }

    @Test func progressLabelsAndFractions() {
        // In whichever language the tests run in (another suite may switch it meanwhile).
        #expect(FileTranscription.Progress.transcribing(done: 3, total: 12).label.contains("3/12"))
        #expect(FileTranscription.Progress.reading(0.404).label.contains("40%"))
        #expect(!FileTranscription.Progress.reading(nil).label.contains("%"))
        #expect(FileTranscription.Progress.transcribing(done: 3, total: 12).fraction == 0.25)
        #expect(FileTranscription.Progress.transcribing(done: 0, total: 0).fraction == nil)
        #expect(FileTranscription.Progress.reading(1.5).fraction == 1)
        #expect(FileTranscription.Progress.cleaningUp.fraction == nil)
        #expect(FileTranscription.Progress.queued.fraction == nil)
    }

    @Test func noNewAudioIsHandedOutWhileDictating() {
        #expect(FileTranscription.outstandingLimit(dictating: false) == FileTranscription.maxOutstandingChunks)
        #expect(FileTranscription.outstandingLimit(dictating: true) == 0)
        #expect(FileTranscription.maxConcurrentRequests < 3)
    }

    @Test func diskFullIsRecognisedThroughWrappers() {
        let posix = NSError(domain: NSPOSIXErrorDomain, code: Int(ENOSPC))
        #expect(FileTranscription.isDiskFull(posix))
        #expect(FileTranscription.isDiskFull(CocoaError(.fileWriteOutOfSpace)))
        #expect(FileTranscription.isDiskFull(NSError(domain: NSCocoaErrorDomain, code: NSFileWriteUnknownError,
                                                     userInfo: [NSUnderlyingErrorKey: posix])))
        #expect(!FileTranscription.isDiskFull(CocoaError(.fileWriteNoPermission)))
        #expect(FileTranscriptionError.writing(posix) == .diskFull)
        #expect(FileTranscriptionError.reading(CocoaError(.fileReadNoPermission)) == .permissionDenied)
        #expect(FileTranscriptionError.reading(CocoaError(.fileReadNoSuchFile)) == .notFound)
        #expect(FileTranscriptionError.reading(NSError(domain: AVFoundationErrorDomain, code: -11828)) == .unsupported(nil))
    }

    // MARK: Reading files

    private func scratch(_ name: String) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("opentypeless-files-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent(name)
    }

    @Test func recordingIsReadBackInBlocks() throws {
        let url = try scratch("audio.wav")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        let samples = (0..<40_000).map { Int16(truncatingIfNeeded: $0 * 7) }
        let writer = try WAVFileWriter(url: url)
        writer.append(Array(samples[..<25_000]))
        writer.append(Array(samples[25_000...]))
        // Not finalized yet, as after a crash: the header still says 0 bytes, so it's read to the end of the file.
        var reader = try WAVFileReader(url: url)
        #expect(reader.sampleCount == samples.count)
        writer.close()

        reader = try WAVFileReader(url: url)
        var read: [Int16] = []
        var blocks = 0
        while let block = try reader.read(maxSamples: 16_000) {
            #expect(block.count <= 16_000)
            read += block
            blocks += 1
        }
        #expect(read == samples)
        #expect(blocks == 3)
        #expect(try reader.read(maxSamples: 16_000) == nil)
    }

    @Test func recordingInAnotherFormatIsRefused() throws {
        let url = try scratch("other.wav")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        try WAV.encode(samples: [1, 2, 3, 4], sampleRate: 44_100).write(to: url)
        #expect(throws: WAVFileReader.ReadError.unsupportedFormat) { try WAVFileReader(url: url) }
        try Data("not audio".utf8).write(to: url)
        #expect(throws: WAVFileReader.ReadError.unsupportedFormat) { try WAVFileReader(url: url) }
    }

    /// A second of a 440 Hz tone at `rate`, in `channels` channels, written by AVFoundation with `format`.
    private func tone(_ url: URL, rate: Double, channels: UInt32, format: AudioFormatID = kAudioFormatLinearPCM) throws {
        var settings: [String: Any] = [AVFormatIDKey: format, AVSampleRateKey: rate, AVNumberOfChannelsKey: channels]
        if format == kAudioFormatLinearPCM {
            settings[AVLinearPCMBitDepthKey] = 16
            settings[AVLinearPCMIsFloatKey] = false
        }
        let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let frames = AVAudioFrameCount(rate)
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames))
        buffer.frameLength = frames
        for channel in 0..<Int(file.processingFormat.channelCount) {
            let data = try #require(buffer.floatChannelData)[channel]
            for i in 0..<Int(frames) { data[i] = Float(0.5 * sin(2 * .pi * 440 * Double(i) / rate)) }
        }
        try file.write(from: buffer)
    }

    @Test(arguments: [("wav", kAudioFormatLinearPCM), ("m4a", kAudioFormatMPEG4AAC)])
    func filesAreDecodedTo16kHzMono(_ type: String, _ format: AudioFormatID) async throws {
        let url = try scratch("tone.\(type)")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        try tone(url, rate: 44_100, channels: 2, format: format)

        #expect(abs(try await AudioFileDecoder.duration(of: url) - 1) < 0.05)
        var blocks = 0
        var samples: [Int16] = []
        let count = try await AudioFileDecoder.decode(url) { block in
            blocks += 1
            samples += block
        }
        #expect(count == samples.count)
        // One second at 16 kHz, give or take the resampler's edges.
        #expect(abs(samples.count - AudioFormat.sampleRate) < 160)
        #expect(blocks > 1)
        // The tone is there, mixed down from both channels.
        #expect(AudioLevel.rms(samples) > 0.1)
        #expect(try await AudioFileDecoder.load(url).count == samples.count)
    }

    @Test func unreadableFilesSayWhy() async throws {
        let url = try scratch("notes.mp3")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        await #expect(throws: FileTranscriptionError.notFound) { try await AudioFileDecoder.duration(of: url) }
        try Data("these are not the samples you are looking for".utf8).write(to: url)
        await #expect(throws: FileTranscriptionError.unsupported(nil)) { try await AudioFileDecoder.decode(url) { _ in } }
    }

    @Test func whatTheCallerThrowsStopsDecoding() async throws {
        let url = try scratch("tone.wav")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        try tone(url, rate: 48_000, channels: 1)
        var calls = 0
        await #expect(throws: FileTranscriptionError.diskFull) {
            try await AudioFileDecoder.decode(url) { _ in
                calls += 1
                throw FileTranscriptionError.diskFull
            }
        }
        #expect(calls == 1)
    }
}

/// Answers each speech-to-text request after a moment, counting how many are out at once.
private final class CountingServer: URLProtocol {
    nonisolated(unsafe) static var current = 0
    nonisolated(unsafe) static var peak = 0
    nonisolated(unsafe) static var requests = 0
    static let lock = NSLock()

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() {}

    override func startLoading() {
        Self.lock.withLock {
            Self.current += 1
            Self.requests += 1
            Self.peak = max(Self.peak, Self.current)
        }
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.05) { [self] in
            Self.lock.withLock { Self.current -= 1 }
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(#"{"text":"words"}"#.utf8))
            client?.urlProtocolDidFinishLoading(self)
        }
    }
}

@Suite(.serialized) struct FilePipelineTests {
    /// Speech with a pause every 6 seconds, so the chunker cuts it into many chunks.
    private func speech(seconds: Double) -> [Int16] {
        (0..<AudioFormat.sampleCount(forSeconds: seconds)).map { i in
            AudioFormat.seconds(forSampleCount: i).truncatingRemainder(dividingBy: 6) < 0.5 ? 0 : Int16(6000 * sin(Double(i) * 0.2))
        }
    }

    @Test func fileRequestsAreCappedAndCountedBeforeTheEnd() async throws {
        CountingServer.lock.withLock { CountingServer.current = 0; CountingServer.peak = 0; CountingServer.requests = 0 }
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [CountingServer.self]
        let client = APIClient(endpoint: .openRouter(apiKey: "test"), session: URLSession(configuration: config))
        let pipeline = TranscriptionPipeline(client: client, options: .init(model: "m"),
                                             maxConcurrentRequests: FileTranscription.maxConcurrentRequests, speculate: false)
        let audio = speech(seconds: 120)
        let reference = Chunker()
        let total = reference.append(audio).count + (reference.finish() == nil ? 0 : 1)

        pipeline.append(audio)
        // Handed out and counted before `finish`, unlike `transcriptionProgress`.
        #expect(pipeline.transcriptionProgress() == nil)
        let counts = pipeline.chunkCounts()
        #expect(counts.finished + counts.outstanding == total - 1)
        #expect(counts.outstanding > FileTranscription.maxConcurrentRequests)

        _ = try await pipeline.finish()
        #expect(pipeline.chunkCounts() == (finished: total, outstanding: 0))
        #expect(CountingServer.lock.withLock { CountingServer.peak } == FileTranscription.maxConcurrentRequests)
        #expect(CountingServer.lock.withLock { CountingServer.requests } == total)
    }

    @Test func cleanUpPartsAreCapped() async throws {
        let transcript = (1...60).map { "我们今天讨论了第\($0)个问题，主要是关于缓存和数据库的性能。" }.joined()
        let gauge = Gauge()
        let outcome = try await SegmentedPolish.run(transcript: transcript, concurrency: 2, polish: { part, _ in
            gauge.enter()
            try await Task.sleep(nanoseconds: 30_000_000)
            gauge.leave()
            return HedgedPolishResult(result: .init(text: part, truncated: false, usage: nil), model: "m", usedBackup: false,
                                      firstTokenSeconds: 0.01, totalSeconds: 0.03)
        })
        #expect(outcome.parts.count >= 5)
        #expect(outcome.fallbacks.isEmpty)
        #expect(gauge.peak == 2)
    }
}

private final class Gauge: @unchecked Sendable {
    private let lock = NSLock()
    private var current = 0
    private(set) var peak = 0

    func enter() { lock.withLock { current += 1; peak = max(peak, current) } }
    func leave() { lock.withLock { current -= 1 } }
}
