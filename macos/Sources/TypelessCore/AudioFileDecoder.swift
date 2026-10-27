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
        guard reader.startReading() else { throw FileTranscriptionError.reading(reader.error ?? CocoaError(.fileReadUnknown)) }
        var total = 0
        do {
            while let buffer = output.copyNextSampleBuffer() {
                try Task.checkCancellation()
                // Decoding an hour of audio takes a while: lets the other work on this shared thread (a dictation's
                // requests among it) go between blocks.
                await Task.yield()
                guard let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
                var samples = [Int16](repeating: 0, count: CMBlockBufferGetDataLength(block) / 2)
                guard !samples.isEmpty else { continue }
                let status = samples.withUnsafeMutableBytes {
                    CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: $0.count, destination: $0.baseAddress!)
                }
                guard status == kCMBlockBufferNoErr else { throw FileTranscriptionError.unsupported(nil) }
                try onSamples(samples)
                total += samples.count
            }
        } catch {
            reader.cancelReading()
            throw error
        }
        if reader.status == .failed { throw FileTranscriptionError.reading(reader.error ?? CocoaError(.fileReadUnknown)) }
        try Task.checkCancellation()
        return total
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
