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
