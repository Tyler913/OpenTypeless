import Foundation
import Testing
@testable import TypelessCore

/// Playing a dictation's recording from History: which records offer it, and where a recording's samples are.
@Suite struct RecordingPlaybackTests {
    /// testdata/playback-cases.json, shared with the Windows tests so both apps play recordings the same way.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/playback-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    private static func bytes(hex: String) -> Data {
        var data = Data()
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            data.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        return data
    }

    @Test func playableMatchesSharedCases() throws {
        for item in try Self.cases()["playable"] as! [[String: Any]] {
            #expect(RecordingPlayback.isPlayable(status: item["status"] as! String, hasAudio: item["hasAudio"] as! Bool)
                    == item["playable"] as! Bool, "\(item)")
        }
    }

    @Test func soundsMatchSharedCases() throws {
        for item in try Self.cases()["sounds"] as! [[String: Any]] {
            let name = item["name"] as! String
            let sound = RecordingPlayback.sound(header: Self.bytes(hex: item["header"] as! String), fileSize: item["fileSize"] as! Int)
            if let expected = item["sound"] as? [String: Any] {
                #expect(sound == RecordingPlayback.SoundRange(offset: expected["offset"] as! Int, byteCount: expected["byteCount"] as! Int,
                                                              headerIsRight: expected["headerIsRight"] as! Bool), "\(name)")
            } else {
                #expect(sound == nil, "\(name)")
            }
        }
    }

    /// The file WAVFileWriter leaves, finalized or not (the app quit while recording), and what a player is then given.
    @Test func recordingsAsTheWriterLeavesThem() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let samples = (0..<16_000).map { Int16(truncatingIfNeeded: $0 * 7) }

        let finished = folder.appendingPathComponent("finished.wav")
        let writer = try WAVFileWriter(url: finished)
        writer.append(samples)
        writer.close()
        let file = try Data(contentsOf: finished)
        let sound = try #require(RecordingPlayback.sound(header: file.prefix(RecordingPlayback.headerBytes), fileSize: file.count))
        #expect(sound == RecordingPlayback.SoundRange(offset: 44, byteCount: 32_000, headerIsRight: true))
        #expect(sound.seconds == 1)

        // Never finalized: the header still says no samples. Given the right header, the samples are all there.
        var unfinished = file
        unfinished.replaceSubrange(40..<44, with: Data(repeating: 0, count: 4))
        let repaired = try #require(RecordingPlayback.sound(header: unfinished.prefix(RecordingPlayback.headerBytes), fileSize: unfinished.count))
        #expect(!repaired.headerIsRight)
        let playable = WAV.header(dataByteCount: repaired.byteCount) + unfinished.subdata(in: repaired.offset..<(repaired.offset + repaired.byteCount))
        #expect(WAV.decodeSamples(playable) == samples)
        #expect(playable == file)
    }
}
