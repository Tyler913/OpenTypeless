import Foundation

/// Playing a dictation's recording back from History, to hear what was said next to what was transcribed. A recording
/// is one `audio.wav`, written as it was spoken (the chunks are cut from it in memory, never saved apart), so the whole
/// file, played once from the start, is what the speaker said. A transcribed file's is the copy the app decoded, never
/// the original.
public enum RecordingPlayback {
    /// Whether History offers to play a record's recording (`status` as in session.json): there is one, and it's no
    /// longer being written. Once recording ends the file is complete, also while the dictation is processed.
    public static func isPlayable(status: String, hasAudio: Bool) -> Bool {
        hasAudio && status != "recording"
    }

    /// How many bytes from the start of the file `sound(header:fileSize:)` is given: the header and any chunk before
    /// the samples fit easily.
    public static let headerBytes = 4096

    /// Where the samples are in a recording's WAV file.
    public struct SoundRange: Equatable, Sendable {
        /// Where the first sample starts, in bytes from the start of the file.
        public var offset: Int
        /// How many bytes of samples there are, a whole number of samples.
        public var byteCount: Int
        /// The header gives that length itself, so the file plays as it is. A recording that was never finalized (the
        /// app quit or crashed while recording) says 0 there, and players stop at once; it's played with a header that
        /// says the right length (`WAV.header(dataByteCount:)`) in front of these bytes.
        public var headerIsRight: Bool

        public init(offset: Int, byteCount: Int, headerIsRight: Bool) {
            self.offset = offset
            self.byteCount = byteCount
            self.headerIsRight = headerIsRight
        }

        public var seconds: Double { AudioFormat.seconds(forSampleCount: byteCount / AudioFormat.bytesPerSample) }
    }

    /// The samples of a 16 kHz mono 16-bit WAV, read from its first bytes (`header`, at least up to the start of the
    /// samples) and its size, as `WAVFileReader` reads them: an unknown length (0, as a recording never finalized
    /// says, or 0xFFFFFFFF from a streaming writer) or one longer than the file is the rest of the file. nil when there
    /// is nothing to play: not such a WAV, or no samples.
    public static func sound(header: Data, fileSize: Int) -> SoundRange? {
        let bytes = [UInt8](header)
        func le32(_ at: Int) -> UInt32 {
            UInt32(bytes[at]) | UInt32(bytes[at + 1]) << 8 | UInt32(bytes[at + 2]) << 16 | UInt32(bytes[at + 3]) << 24
        }
        func le16(_ at: Int) -> UInt16 { UInt16(bytes[at]) | UInt16(bytes[at + 1]) << 8 }
        guard bytes.count >= 12, bytes[0..<4].elementsEqual("RIFF".utf8), bytes[8..<12].elementsEqual("WAVE".utf8) else { return nil }
        var offset = 12
        var formatOK = false
        while offset + 8 <= bytes.count {
            let id = bytes[offset..<(offset + 4)]
            let declared = le32(offset + 4)
            let body = offset + 8
            if id.elementsEqual("fmt ".utf8) {
                guard body + 16 <= bytes.count else { return nil }
                let tag = le16(body), channels = le16(body + 2), rate = le32(body + 4), bits = le16(body + 14)
                // 1 = PCM; 0xFFFE = WAVE_FORMAT_EXTENSIBLE, which some writers (Media Foundation) use for plain PCM too.
                formatOK = (tag == 1 || tag == 0xFFFE) && channels == 1 && rate == UInt32(AudioFormat.sampleRate) && bits == 16
            } else if id.elementsEqual("data".utf8) {
                guard formatOK, fileSize > body else { return nil }
                let available = fileSize - body
                let known = declared != 0 && declared != UInt32.max && Int(declared) <= available
                let length = known ? Int(declared) : available
                let count = length - length % AudioFormat.bytesPerSample
                guard count > 0 else { return nil }
                return SoundRange(offset: body, byteCount: count, headerIsRight: known && count == length)
            }
            offset = body + Int(declared) + Int(declared % 2)
        }
        return nil
    }
}
