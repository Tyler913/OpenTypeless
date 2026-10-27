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
        #expect(FileTranscription.Progress.reading(0.404).label.contains("40"))
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

    @Test func givingUpIsForRefusalsAndRunsOfFailures() {
        #expect(!FileTranscription.shouldGiveUp(ChunkCounts(answered: 10, failed: 2, outstanding: 2, failureStreak: 2)))
        #expect(FileTranscription.shouldGiveUp(ChunkCounts(answered: 10, failed: 3, outstanding: 0,
                                                             failureStreak: FileTranscription.maxConsecutiveFailures)))
        #expect(FileTranscription.shouldGiveUp(ChunkCounts(answered: 0, failed: 1, outstanding: 3, refused: 1, failureStreak: 1)))
        #expect(APIError.http(status: 401, message: "").refusesEveryRequest)
        #expect(APIError.missingAPIKey("x").refusesEveryRequest)
        #expect(!APIError.http(status: 400, message: "").refusesEveryRequest)
        #expect(!APIError.http(status: 500, message: "").refusesEveryRequest)
        #expect(!APIError.timeout(seconds: 30).refusesEveryRequest)
    }

    @Test func roomForTheRecording() {
        // An hour is about 115 MB, and some to spare.
        #expect(FileTranscription.requiredBytes(seconds: 3600) == 3600 * 32_000 + 64 * 1_048_576)
        #expect(FileTranscription.requiredBytes(seconds: 0) == 64 * 1_048_576)
    }

    @Test func interruptedRecordsSayWhatToDo() {
        let reading = FileTranscription.interruptedMessage(isFile: true, hasAudio: false)
        let retry = FileTranscription.interruptedMessage(isFile: false, hasAudio: true)
        #expect(reading != retry)
        #expect(FileTranscription.interruptedMessage(isFile: true, hasAudio: true) == retry)
        #expect(FileTranscription.interruptedMessage(isFile: false, hasAudio: false) == retry)
    }

    @Test func everyErrorHasItsOwnMessage() {
        let errors: [FileTranscriptionError] = [.notFound, .permissionDenied, .unsupported(nil), .unsupported("x"),
                                                .noAudioTrack, .noSound, .protected, .diskFull, .writeFailed("x")]
        let messages = Set(errors.compactMap(\.errorDescription))
        #expect(messages.count == errors.count)
    }

    @Test func chunksDontDependOnHowTheAudioIsSplit() {
        let audio = fileSpeech(seconds: 95)
        func count(_ block: Int) -> Int {
            let chunker = Chunker()
            var chunks = 0
            var start = 0
            while start < audio.count {
                chunks += chunker.append(Array(audio[start..<min(start + block, audio.count)])).count
                start += block
            }
            return chunks + (chunker.finish() == nil ? 0 : 1)
        }
        let whole = count(audio.count)
        #expect(whole > 5)
        #expect(count(AudioFormat.sampleCount(forSeconds: FileTranscription.blockSeconds)) == whole)
        #expect(count(1_234) == whole)
    }

    /// A WAV put together by hand: `chunks` in order, each padded to an even length as RIFF wants.
    private func riff(_ chunks: [(String, Data)]) -> Data {
        var body = Data("WAVE".utf8)
        for (id, content) in chunks {
            body += Data(id.utf8)
            body.appendLE(UInt32(content.count))
            body += content
            if content.count % 2 == 1 { body.append(0) }
        }
        var data = Data("RIFF".utf8)
        data.appendLE(UInt32(body.count))
        return data + body
    }

    private func format(tag: UInt16 = 1, extra: Int = 0) -> Data {
        var data = Data()
        data.appendLE(tag)
        data.appendLE(UInt16(1))
        data.appendLE(UInt32(16_000))
        data.appendLE(UInt32(32_000))
        data.appendLE(UInt16(2))
        data.appendLE(UInt16(16))
        if extra > 0 {
            data.appendLE(UInt16(extra))
            data += Data(count: extra)
        }
        return data
    }

    private func samples(_ values: [Int16]) -> Data {
        var data = Data()
        for value in values { data.appendLE(value) }
        return data
    }

    private func readAll(_ data: Data) throws -> [Int16] {
        let url = try scratch("edge.wav")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        try data.write(to: url)
        let reader = try WAVFileReader(url: url)
        var read: [Int16] = []
        while let block = try reader.read(maxSamples: 2) { read += block }
        #expect(read.count == reader.sampleCount)
        return read
    }

    @Test func recordingsFromOtherWritersAreReadToo() throws {
        let audio = samples([1, -2, 3])
        // Other chunks before the audio, one of an odd size (padded).
        #expect(try readAll(riff([("LIST", Data([1, 2, 3])), ("fmt ", format()), ("JUNK", Data(count: 4)), ("data", audio)]))
                == [1, -2, 3])
        // WAVE_FORMAT_EXTENSIBLE, as Media Foundation writes plain PCM.
        #expect(try readAll(riff([("fmt ", format(tag: 0xFFFE, extra: 22)), ("data", audio)])) == [1, -2, 3])
        // Something after the audio isn't read as audio.
        #expect(try readAll(riff([("fmt ", format()), ("data", audio), ("LIST", Data([9, 9, 9, 9]))])) == [1, -2, 3])
        // An odd byte at the end of the audio is no sample.
        #expect(try readAll(riff([("fmt ", format()), ("data", audio + Data([7]))])) == [1, -2, 3])
        // No audio at all.
        #expect(try readAll(riff([("fmt ", format()), ("data", Data())])) == [])
    }

    @Test func aCutOffRecordingIsReadAsFarAsItGoes() throws {
        var data = riff([("fmt ", format()), ("data", samples([5, 6, 7, 8]))])
        // Says 1000 bytes of audio, has 8; then lose the last sample and a half.
        data.replaceSubrange((data.count - 12)..<(data.count - 8), with: { var d = Data(); d.appendLE(UInt32(1000)); return d }())
        data.removeLast(3)
        #expect(try readAll(data) == [5, 6])
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

/// Speech with a pause every 6 seconds, so the chunker cuts it into many chunks.
private func fileSpeech(seconds: Double) -> [Int16] {
    (0..<AudioFormat.sampleCount(forSeconds: seconds)).map { i in
        AudioFormat.seconds(forSampleCount: i).truncatingRemainder(dividingBy: 6) < 0.5 ? 0 : Int16(6000 * sin(Double(i) * 0.2))
    }
}

/// A speech-to-text server: answers "words", or fails with `status`, after the first `failFirst` requests fail with 500.
/// While `holding`, requests wait until `release`, so the test sees how many are out at once without timing.
private final class MockServer: URLProtocol {
    nonisolated(unsafe) static var status = 200
    nonisolated(unsafe) static var failFirst = 0
    nonisolated(unsafe) static var holding = false
    nonisolated(unsafe) static var held: [MockServer] = []
    nonisolated(unsafe) static var current = 0
    nonisolated(unsafe) static var peak = 0
    nonisolated(unsafe) static var requests = 0
    static let lock = NSLock()
    private var answered = false

    static func reset(status: Int = 200, failFirst: Int = 0, holding: Bool = false) {
        lock.withLock {
            self.status = status
            self.failFirst = failFirst
            self.holding = holding
            held = []
            current = 0
            peak = 0
            requests = 0
        }
    }

    static func release() {
        let waiting = lock.withLock { () -> [MockServer] in
            holding = false
            defer { held = [] }
            return held
        }
        waiting.forEach { $0.answer() }
    }

    static var counts: (current: Int, peak: Int, requests: Int) { lock.withLock { (current, peak, requests) } }

    static func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockServer.self]
        return URLSession(configuration: config)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let hold = Self.lock.withLock { () -> Bool in
            Self.current += 1
            Self.requests += 1
            Self.peak = max(Self.peak, Self.current)
            if Self.holding { Self.held.append(self) }
            return Self.holding
        }
        if !hold { answer() }
    }

    override func stopLoading() {
        Self.lock.withLock {
            guard !answered else { return }
            answered = true
            Self.current -= 1
            Self.held.removeAll { $0 === self }
        }
    }

    private func answer() {
        let status = Self.lock.withLock { () -> Int? in
            guard !answered else { return nil }
            answered = true
            Self.current -= 1
            if Self.failFirst > 0 {
                Self.failFirst -= 1
                return 500
            }
            return Self.status
        }
        guard let status else { return }
        let body = status == 200 ? #"{"text":"words"}"# : #"{"error":{"message":"no"}}"#
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
}

@Suite(.serialized) struct FilePipelineTests {
    private func pipeline(attempts: Int = 4, maxConcurrentRequests: Int = FileTranscription.maxConcurrentRequests,
                          preset: [Int: String] = [:]) -> TranscriptionPipeline {
        TranscriptionPipeline(client: APIClient(endpoint: .openRouter(apiKey: "test"), session: MockServer.session()),
                              options: .init(model: "m"), policy: RetryPolicy(maxAttempts: attempts, baseDelay: 0.01),
                              preset: preset, maxConcurrentRequests: maxConcurrentRequests, speculate: false)
    }

    private func chunkCount(_ audio: [Int16]) -> Int {
        let chunker = Chunker()
        return chunker.append(audio).count + (chunker.finish() == nil ? 0 : 1)
    }

    /// Waits (up to 10 s) until `condition` holds.
    private func eventually(_ condition: () -> Bool) async throws {
        for _ in 0..<1000 where !condition() { try await Task.sleep(nanoseconds: 10_000_000) }
    }

    /// A recording of `audio` on disk, read back as a file's is.
    private func recording(_ audio: [Int16]) throws -> (WAVFileReader, URL) {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("opentypeless-feed-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("audio.wav")
        let writer = try WAVFileWriter(url: url)
        writer.append(audio)
        writer.close()
        return (try WAVFileReader(url: url), dir)
    }

    @Test func fileRequestsAreCappedAndCountedBeforeTheEnd() async throws {
        MockServer.reset(holding: true)
        let pipeline = pipeline()
        let audio = fileSpeech(seconds: 120)
        let total = chunkCount(audio)

        pipeline.append(audio)
        // Handed out and counted before `finish`, unlike `transcriptionProgress`.
        #expect(pipeline.transcriptionProgress() == nil)
        let counts = pipeline.chunkCounts()
        #expect(counts.answered + counts.outstanding == total - 1)
        #expect(counts.outstanding > FileTranscription.maxConcurrentRequests)
        // As many as the cap are sent, and no more while they're out.
        try await eventually { MockServer.counts.current == FileTranscription.maxConcurrentRequests }
        try await Task.sleep(nanoseconds: 100_000_000)
        #expect(MockServer.counts.current == FileTranscription.maxConcurrentRequests)
        MockServer.release()

        _ = try await pipeline.finish()
        #expect(pipeline.chunkCounts() == ChunkCounts(answered: total, failed: 0, outstanding: 0))
        #expect(MockServer.counts.peak == FileTranscription.maxConcurrentRequests)
        #expect(MockServer.counts.requests == total)
        #expect(pipeline.heldChunkCount == 0)
    }

    @Test func onlyFailedChunksKeepTheirAudioForTheLastRound() async throws {
        MockServer.reset(failFirst: 2)
        let pipeline = pipeline(attempts: 1, preset: [0: "kept"])
        let audio = fileSpeech(seconds: 60)
        let total = chunkCount(audio)
        pipeline.append(audio)
        try await eventually { pipeline.chunkCounts().outstanding == 0 }
        // The preset chunk isn't sent or kept; the two that failed are kept; the rest are let go.
        let counts = pipeline.chunkCounts()
        #expect(counts.answered == total - 3 && counts.failed == 2 && counts.outstanding == 0)
        #expect(pipeline.heldChunkCount == 2)

        let text = try await pipeline.finish()
        #expect(text.hasPrefix("kept"))
        #expect(MockServer.counts.requests == total - 1 + 2)
        #expect(pipeline.heldChunkCount == 0)
    }

    @Test func aFileStopsWhenTheProviderRefuses() async throws {
        MockServer.reset(status: 401)
        let audio = fileSpeech(seconds: 300)
        let (reader, dir) = try recording(audio)
        defer { try? FileManager.default.removeItem(at: dir) }
        let pipeline = pipeline()
        await #expect(throws: PipelineFailure.self) { try await FileTranscription.feed(reader, to: pipeline, dictating: { false }) }
        // Stopped after the first refusal, not after sending every chunk.
        #expect(MockServer.counts.requests <= FileTranscription.maxOutstandingChunks + 2)
        #expect(chunkCount(audio) > 3 * FileTranscription.maxOutstandingChunks)
    }

    @Test func aFileStopsAfterARunOfFailures() async throws {
        MockServer.reset(status: 500)
        let audio = fileSpeech(seconds: 300)
        let (reader, dir) = try recording(audio)
        defer { try? FileManager.default.removeItem(at: dir) }
        let pipeline = pipeline(attempts: 1)
        do {
            _ = try await FileTranscription.feed(reader, to: pipeline, dictating: { false })
            Issue.record("expected a failure")
        } catch let failure as PipelineFailure {
            #expect(failure.failedChunks.count >= FileTranscription.maxConsecutiveFailures)
        }
        #expect(MockServer.counts.requests < chunkCount(audio))
    }

    @Test func aDictationNeverStopsEarly() async throws {
        // The same failures, in the pipeline as a dictation uses it: every chunk is sent, and again in the last round.
        MockServer.reset(status: 500)
        let audio = fileSpeech(seconds: 60)
        let total = chunkCount(audio)
        let pipeline = pipeline(attempts: 1, maxConcurrentRequests: 3)
        pipeline.append(audio)
        await #expect(throws: PipelineFailure.self) { try await pipeline.finish() }
        #expect(MockServer.counts.requests == 2 * total)
    }

    @Test func aFileWaitsWhileDictating() async throws {
        MockServer.reset()
        let (reader, dir) = try recording(fileSpeech(seconds: 40))
        defer { try? FileManager.default.removeItem(at: dir) }
        let pipeline = pipeline()
        let dictating = Switch(true)
        let feeding = Task { try await FileTranscription.feed(reader, to: pipeline, dictating: { dictating.value }) }
        try await Task.sleep(nanoseconds: 500_000_000)
        #expect(MockServer.counts.requests == 0)
        dictating.value = false
        #expect(try await feeding.value.contains("words"))
    }

    @Test func cleanUpPartsAreCapped() async throws {
        let transcript = (1...60).map { "我们今天讨论了第\($0)个问题，主要是关于缓存和数据库的性能。" }.joined()
        func run(_ concurrency: Int?) async throws -> (SegmentedPolishResult, Int) {
            let gauge = Gauge()
            let outcome = try await SegmentedPolish.run(transcript: transcript, concurrency: concurrency, polish: { part, partial in
                gauge.enter()
                partial?(part)
                try await Task.sleep(nanoseconds: 5_000_000)
                gauge.leave()
                return Self.answer(part)
            })
            return (outcome, gauge.peak)
        }
        let (all, _) = try await run(nil)
        let (two, peakTwo) = try await run(2)
        let (one, peakOne) = try await run(1)
        #expect(all.parts.count >= 5)
        #expect(peakTwo <= 2 && peakOne == 1)
        // The same text in the same order, however many are out at once.
        #expect(two.text == all.text && one.text == all.text)
        #expect(one.fallbacks.isEmpty)
    }

    @Test func cleanUpPartsWaitingTheirTurnDontStartOnceCancelled() async throws {
        let transcript = (1...60).map { "我们今天讨论了第\($0)个问题，主要是关于缓存和数据库的性能。" }.joined()
        let gauge = Gauge()
        let running = Task {
            try await SegmentedPolish.run(transcript: transcript, concurrency: 1, polish: { part, _ in
                gauge.enter()
                defer { gauge.leave() }
                try await Task.sleep(nanoseconds: 5_000_000_000)
                return Self.answer(part)
            })
        }
        try await eventually { gauge.entered == 1 }
        running.cancel()
        await #expect(throws: (any Error).self) { try await running.value }
        #expect(gauge.entered == 1)
    }

    @Test func aFilesCleanUpWaitsWhileDictating() async throws {
        let transcript = (1...20).map { "我们今天讨论了第\($0)个问题，主要是关于缓存和数据库的性能。" }.joined()
        let gauge = Gauge()
        let dictating = Switch(true)
        let running = Task {
            try await SegmentedPolish.run(transcript: transcript, concurrency: 2, holdWhile: { dictating.value }, polish: { part, _ in
                gauge.enter()
                defer { gauge.leave() }
                return Self.answer(part)
            })
        }
        try await Task.sleep(nanoseconds: 500_000_000)
        #expect(gauge.entered == 0)
        dictating.value = false
        #expect(try await running.value.fallbacks.isEmpty)
    }

    private static func answer(_ part: String) -> HedgedPolishResult {
        HedgedPolishResult(result: .init(text: part, truncated: false, usage: nil), model: "m", usedBackup: false,
                           firstTokenSeconds: 0.01, totalSeconds: 0.01)
    }
}

private final class Switch: @unchecked Sendable {
    private let lock = NSLock()
    private var _value: Bool
    init(_ value: Bool) { _value = value }
    var value: Bool {
        get { lock.withLock { _value } }
        set { lock.withLock { _value = newValue } }
    }
}

private final class Gauge: @unchecked Sendable {
    private let lock = NSLock()
    private var current = 0
    private var _peak = 0
    private var _entered = 0
    var peak: Int { lock.withLock { _peak } }
    var entered: Int { lock.withLock { _entered } }

    func enter() { lock.withLock { current += 1; _entered += 1; _peak = max(_peak, current) } }
    func leave() { lock.withLock { current -= 1 } }
}
