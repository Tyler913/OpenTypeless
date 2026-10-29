import Foundation

public enum AudioFormat {
    /// Everything downstream works on 16 kHz mono signed 16-bit PCM.
    public static let sampleRate = 16_000
    public static let bytesPerSample = 2

    public static func seconds(forSampleCount count: Int) -> Double {
        Double(count) / Double(sampleRate)
    }

    public static func sampleCount(forSeconds seconds: Double) -> Int {
        Int(seconds * Double(sampleRate))
    }
}

public enum WAV {
    /// 44-byte canonical RIFF header followed by little-endian PCM16 samples.
    public static func encode(samples: [Int16], sampleRate: Int = AudioFormat.sampleRate) -> Data {
        var data = header(dataByteCount: samples.count * 2, sampleRate: sampleRate)
        samples.withUnsafeBufferPointer { buffer in
            // Apple Silicon and Intel are both little-endian, so the raw buffer is already in WAV order.
            data.append(UnsafeBufferPointer(start: UnsafeRawPointer(buffer.baseAddress!)
                .assumingMemoryBound(to: UInt8.self), count: buffer.count * 2))
        }
        return data
    }

    public static func header(dataByteCount: Int, sampleRate: Int = AudioFormat.sampleRate) -> Data {
        let channels: UInt16 = 1
        let bitsPerSample: UInt16 = 16
        let byteRate = UInt32(sampleRate) * UInt32(channels) * UInt32(bitsPerSample) / 8
        let blockAlign = channels * bitsPerSample / 8

        var data = Data(capacity: 44 + dataByteCount)
        data.append(contentsOf: Array("RIFF".utf8))
        data.appendLE(UInt32(36 + dataByteCount))
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8))
        data.appendLE(UInt32(16))
        data.appendLE(UInt16(1)) // PCM
        data.appendLE(channels)
        data.appendLE(UInt32(sampleRate))
        data.appendLE(byteRate)
        data.appendLE(blockAlign)
        data.appendLE(bitsPerSample)
        data.append(contentsOf: Array("data".utf8))
        data.appendLE(UInt32(dataByteCount))
        return data
    }

    /// Reads PCM16 mono samples back out of a canonical WAV (as written by `encode` / `WAVFileWriter`).
    public static func decodeSamples(_ data: Data) -> [Int16]? {
        guard data.count >= 44,
              data.prefix(4) == Data("RIFF".utf8),
              data.subdata(in: 8..<12) == Data("WAVE".utf8) else { return nil }
        // Walk chunks to find "data" so files with extra chunks (e.g. from `afconvert`) still work.
        var offset = 12
        while offset + 8 <= data.count {
            let id = data.subdata(in: offset..<(offset + 4))
            let declared = data.readLE32(at: offset + 4)
            let size = Int(declared)
            let body = offset + 8
            if id == Data("data".utf8) {
                // A recording that was never finalized (the app quit or crashed mid-way) still says 0 here, streaming
                // writers put 0xFFFFFFFF, and a cut-off file says more than it has: then the audio is the rest of the file.
                let unknown = declared == 0 || declared == UInt32.max || body + size > data.count
                let end = unknown ? data.count : body + size
                let count = (end - body) / 2
                var samples = [Int16](repeating: 0, count: count)
                _ = samples.withUnsafeMutableBytes { dest in
                    data.copyBytes(to: dest, from: body..<(body + count * 2))
                }
                return samples
            }
            offset = body + size + (size % 2)
        }
        return nil
    }
}

/// Streams samples to disk as they arrive so a crash mid-recording never loses audio.
public final class WAVFileWriter {
    private let handle: FileHandle
    private var dataBytes = 0
    public let url: URL
    /// The first write that failed (the disk filled up, the drive went away). Nothing more is written after it: the
    /// dictation goes on from the audio in memory, and the file keeps what reached it.
    public private(set) var failure: Error?

    public init(url: URL) throws {
        self.url = url
        FileManager.default.createFile(atPath: url.path, contents: WAV.header(dataByteCount: 0))
        handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
    }

    public func append(_ samples: [Int16]) {
        guard !samples.isEmpty, failure == nil else { return }
        let bytes = samples.withUnsafeBytes { Data($0) }
        // The throwing calls: the old `write(_:)` raises an Objective-C exception on a full disk, which kills the app.
        do {
            try handle.write(contentsOf: bytes)
            dataBytes += bytes.count
        } catch {
            failure = error
        }
    }

    /// Patches the RIFF/data sizes so the file is valid. Safe to call more than once.
    public func finalize() {
        do {
            try handle.seek(toOffset: 4)
            try handle.write(contentsOf: Data(le: UInt32(36 + dataBytes)))
            try handle.seek(toOffset: 40)
            try handle.write(contentsOf: Data(le: UInt32(dataBytes)))
            try handle.seekToEnd()
            try handle.synchronize()
        } catch {
            // The header keeps saying 0 bytes, which `WAV.decodeSamples` reads as "to the end of the file".
            if failure == nil { failure = error }
        }
    }

    public func close() {
        finalize()
        try? handle.close()
    }
}

/// Reads a 16 kHz mono PCM16 WAV (as `WAVFileWriter` writes it) a block at a time, so a recording an hour long is never
/// all in memory: a transcribed file's recording is handed to the pipeline this way (see FileTranscription).
public final class WAVFileReader {
    public enum ReadError: Error, Equatable {
        /// Not a WAV, or not 16 kHz mono 16-bit PCM.
        case unsupportedFormat
    }

    /// How many samples the file holds.
    public let sampleCount: Int
    private let handle: FileHandle
    private var remaining: Int

    public init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
        do {
            let fileSize = Int(try handle.seekToEnd())
            try handle.seek(toOffset: 0)
            guard let riff = try handle.read(upToCount: 12), riff.count == 12,
                  riff.prefix(4) == Data("RIFF".utf8), riff.subdata(in: 8..<12) == Data("WAVE".utf8)
            else { throw ReadError.unsupportedFormat }
            // Walks the chunks to "data", checking "fmt " on the way (files from other writers carry extra chunks).
            var offset = 12
            var formatOK = false
            var dataBytes: Int?
            while offset + 8 <= fileSize {
                try handle.seek(toOffset: UInt64(offset))
                guard let header = try handle.read(upToCount: 8), header.count == 8 else { break }
                let declared = header.readLE32(at: 4)
                let body = offset + 8
                if header.prefix(4) == Data("fmt ".utf8) {
                    guard let format = try handle.read(upToCount: 16), format.count == 16 else { break }
                    let tag = UInt16(format[0]) | UInt16(format[1]) << 8
                    let channels = UInt16(format[2]) | UInt16(format[3]) << 8
                    let rate = format.readLE32(at: 4)
                    let bits = UInt16(format[14]) | UInt16(format[15]) << 8
                    // 1 = PCM; 0xFFFE = WAVE_FORMAT_EXTENSIBLE, which some writers use for plain PCM too.
                    formatOK = (tag == 1 || tag == 0xFFFE) && channels == 1 && rate == UInt32(AudioFormat.sampleRate) && bits == 16
                } else if header.prefix(4) == Data("data".utf8) {
                    // Never finalized (the app quit mid-way), written by a streaming writer, or cut off: then the audio
                    // is the rest of the file (as `WAV.decodeSamples` reads it).
                    let unknown = declared == 0 || declared == UInt32.max || body + Int(declared) > fileSize
                    dataBytes = unknown ? fileSize - body : Int(declared)
                    break
                }
                offset = body + Int(declared) + Int(declared % 2)
            }
            guard formatOK, let dataBytes else { throw ReadError.unsupportedFormat }
            remaining = dataBytes - dataBytes % 2
            sampleCount = remaining / 2
        } catch {
            try? handle.close()
            throw error
        }
    }

    deinit { try? handle.close() }

    /// The next block of at most `maxSamples` samples; nil once the whole file has been read.
    public func read(maxSamples: Int) throws -> [Int16]? {
        guard remaining > 0 else { return nil }
        let wanted = min(remaining, max(1, maxSamples) * 2)
        guard let data = try handle.read(upToCount: wanted), data.count >= 2 else {
            remaining = 0
            return nil
        }
        remaining -= data.count
        var samples = [Int16](repeating: 0, count: data.count / 2)
        _ = samples.withUnsafeMutableBytes { data.copyBytes(to: $0, count: $0.count) }
        return samples
    }
}

extension Data {
    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        append(Data(le: value))
    }

    init<T: FixedWidthInteger>(le value: T) {
        var little = value.littleEndian
        self = Swift.withUnsafeBytes(of: &little) { Data($0) }
    }

    func readLE32(at offset: Int) -> UInt32 {
        var value: UInt32 = 0
        for i in 0..<4 { value |= UInt32(self[self.startIndex + offset + i]) << (8 * i) }
        return value
    }
}
