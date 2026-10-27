#if canImport(AVFoundation)
import AVFoundation
import Foundation

/// Reads the sound of an audio or video file (m4a, mp3, wav, aiff, caf, mp4, mov… whatever AVFoundation plays) as
/// 16 kHz mono 16-bit samples, a block at a time: the app writes them to the record's recording as they come, so an hour
/// of audio is never all in memory, and the command line's `--transcribe-file` collects them (`load`). A video's first
/// audio track is read; several channels are mixed down to one.
public enum AudioFileDecoder {
    /// The file's length in seconds, once it's known to have an audio track AVFoundation can open; otherwise why not
    /// (a `FileTranscriptionError`).
    public static func duration(of url: URL) async throws -> Double {
        let (asset, _) = try await audioTrack(of: url)
        let seconds = (try? await asset.load(.duration))?.seconds ?? 0
        return seconds.isFinite ? max(0, seconds) : 0
    }

    /// Hands the whole sound of `url` to `onSamples`, a block of a few thousand samples at a time, and returns how many
    /// samples there were. Throws a `FileTranscriptionError` when the file can't be read (or what `onSamples` threw),
    /// and `CancellationError` when the task is cancelled.
    @discardableResult
    public static func decode(_ url: URL, onSamples: ([Int16]) throws -> Void) async throws -> Int {
        let (asset, track) = try await audioTrack(of: url)
        let reader: AVAssetReader
        do {
            reader = try AVAssetReader(asset: asset)
        } catch {
            throw FileTranscriptionError.reading(error)
        }
        var mono = AudioChannelLayout()
        mono.mChannelLayoutTag = kAudioChannelLayoutTag_Mono
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: AudioFormat.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVChannelLayoutKey: Data(bytes: &mono, count: MemoryLayout<AudioChannelLayout>.size),
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ])
        output.alwaysCopiesSampleData = false
        guard reader.canAdd(output) else { throw FileTranscriptionError.unsupported(nil) }
        reader.add(output)
        let reading = Reading(reader: reader, output: output)
        guard await reading.start() else {
            throw FileTranscriptionError.reading(reader.error ?? CocoaError(.fileReadUnknown))
        }
        var total = 0
        do {
            while true {
                try Task.checkCancellation()
                guard let block = try await reading.next() else { break }
                guard !block.isEmpty else { continue }
                try onSamples(block)
                total += block.count
            }
        } catch {
            reading.cancel()
            throw error
        }
        if reader.status == .failed { throw FileTranscriptionError.reading(reader.error ?? CocoaError(.fileReadUnknown)) }
        try Task.checkCancellation()
        return total
    }

    /// An AVAssetReader asked on a queue of its own. Asking it for the next block holds the thread until the block is
    /// decoded, and AVFoundation may need Swift concurrency's shared threads to get there: asked from one of those, a few
    /// files read at once can take every one of them and never finish (CI's three-core runners did). So the reading
    /// happens here, and the task only waits for it.
    private final class Reading: @unchecked Sendable {
        private let reader: AVAssetReader
        private let output: AVAssetReaderTrackOutput
        private let queue = DispatchQueue(label: "OpenTypeless.AudioFileDecoder")

        init(reader: AVAssetReader, output: AVAssetReaderTrackOutput) {
            self.reader = reader
            self.output = output
        }

        func start() async -> Bool {
            await withCheckedContinuation { continuation in
                queue.async { continuation.resume(returning: self.reader.startReading()) }
            }
        }

        /// The next block of samples (empty when a buffer had none), or nil at the end. Cancelling the task cancels the
        /// reading, which lets a decoder that hangs go, so Cancel always ends a file's reading.
        func next() async throws -> [Int16]? {
            try await withTaskCancellationHandler {
                try await nextBlock()
            } onCancel: {
                reader.cancelReading()
            }
        }

        private func nextBlock() async throws -> [Int16]? {
            try await withCheckedThrowingContinuation { continuation in
                queue.async {
                    guard let buffer = self.output.copyNextSampleBuffer() else { return continuation.resume(returning: nil) }
                    guard let block = CMSampleBufferGetDataBuffer(buffer) else { return continuation.resume(returning: []) }
                    var samples = [Int16](repeating: 0, count: CMBlockBufferGetDataLength(block) / 2)
                    guard !samples.isEmpty else { return continuation.resume(returning: []) }
                    let status = samples.withUnsafeMutableBytes {
                        CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: $0.count, destination: $0.baseAddress!)
                    }
                    if status == kCMBlockBufferNoErr {
                        continuation.resume(returning: samples)
                    } else {
                        continuation.resume(throwing: FileTranscriptionError.unsupported(nil))
                    }
                }
            }
        }

        func cancel() {
            queue.async { self.reader.cancelReading() }
        }
    }

    /// The whole sound of `url` at once, for the command line (the app streams it with `decode`).
    public static func load(_ url: URL) async throws -> [Int16] {
        var samples: [Int16] = []
        try await decode(url) { samples.append(contentsOf: $0) }
        return samples
    }

    /// The file's first audio track. A file AVFoundation opens with tracks but none of them audio (a silent video)
    /// has no audio track; one it opens with no tracks at all, or not at all, isn't a media file it can read; a
    /// copy-protected one can't be read either, and says so.
    private static func audioTrack(of url: URL) async throws -> (AVURLAsset, AVAssetTrack) {
        try checkReadable(url)
        let asset = AVURLAsset(url: url)
        do {
            let all = try await asset.load(.tracks)
            guard let track = try await asset.loadTracks(withMediaType: .audio).first else {
                throw all.isEmpty ? FileTranscriptionError.unsupported(nil) : FileTranscriptionError.noAudioTrack
            }
            // Bought music and rented films: AVAssetReader can't read them, and would only say it failed.
            if try await asset.load(.hasProtectedContent) { throw FileTranscriptionError.protected }
            return (asset, track)
        } catch {
            throw FileTranscriptionError.reading(error)
        }
    }

    /// Tells a missing file and one the user may not read apart from a damaged one, which AVFoundation doesn't.
    private static func checkReadable(_ url: URL) throws {
        guard FileManager.default.fileExists(atPath: url.path) else { throw FileTranscriptionError.notFound }
        do {
            try FileHandle(forReadingFrom: url).close()
        } catch {
            throw FileTranscriptionError.reading(error)
        }
    }
}
#endif
