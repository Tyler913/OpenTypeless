import AVFoundation
import Foundation
import os
import TypelessCore

/// Plays a dictation's recording in History (see `RecordingPlayback`), on the output device the system uses. One at a
/// time: starting another stops it, and so do a dictation starting (the microphone would hear it), History going away
/// (another page, the window closed, another dictation selected) and the record or its recording being deleted.
@MainActor
final class RecordingPlayer: ObservableObject {
    static let shared = RecordingPlayer()

    /// The record whose recording is playing or paused; nil when none is.
    @Published private(set) var recordID: String?
    @Published private(set) var isPlaying = false
    /// How far it has got and how long it is, in seconds.
    @Published private(set) var elapsed: Double = 0
    @Published private(set) var duration: Double = 0
    /// Why the last recording asked for couldn't be played.
    @Published var failure: String?

    private var player: AVAudioPlayer?
    private var ticker: Timer?
    private let log = Logger(subsystem: "local.opentypeless.app", category: "playback")

    private init() {}

    /// Plays the record's recording, or pauses it or carries on when it's the one already playing.
    func toggle(_ record: DictationRecord) {
        if record.id == recordID, let player {
            if player.isPlaying {
                player.pause()
                isPlaying = false
                ticker?.invalidate()
            } else {
                play(player)
            }
            return
        }
        stop()
        do {
            let player = try Self.player(for: record.audioURL)
            self.player = player
            recordID = record.id
            duration = player.duration
            elapsed = 0
            play(player)
        } catch {
            log.debug("couldn't play \(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            failure = L("录音文件已损坏或无法读取。", "The recording is damaged or can't be read.")
        }
    }

    func stop() {
        ticker?.invalidate()
        ticker = nil
        player?.stop()
        player = nil
        recordID = nil
        isPlaying = false
        elapsed = 0
        duration = 0
    }

    /// Stops it if it's this record's recording that is playing or paused.
    func stop(recordID id: String) {
        if recordID == id { stop() }
    }

    private func play(_ player: AVAudioPlayer) {
        guard player.play() else {
            stop()
            failure = L("录音文件已损坏或无法读取。", "The recording is damaged or can't be read.")
            return
        }
        isPlaying = true
        ticker?.invalidate()
        // Moves the time on, and notices the end: the player stops by itself there.
        let ticker = Timer(timeInterval: 0.2, repeats: true) { _ in
            Task { @MainActor in RecordingPlayer.shared.tick() }
        }
        RunLoop.main.add(ticker, forMode: .common)
        self.ticker = ticker
    }

    private func tick() {
        guard let player, isPlaying else { return }
        if player.isPlaying {
            elapsed = player.currentTime
        } else {
            stop()
        }
    }

    /// A player for the recording: the file itself, or, for one never finalized (the app quit while recording), its
    /// samples behind a header that says how long they are, since players stop at once at a length of 0.
    private static func player(for url: URL) throws -> AVAudioPlayer {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let size = Int(try handle.seekToEnd())
        try handle.seek(toOffset: 0)
        let header = try handle.read(upToCount: RecordingPlayback.headerBytes) ?? Data()
        guard let sound = RecordingPlayback.sound(header: header, fileSize: size) else { throw CocoaError(.fileReadCorruptFile) }
        let player: AVAudioPlayer
        if sound.headerIsRight {
            player = try AVAudioPlayer(contentsOf: url)
        } else {
            try handle.seek(toOffset: UInt64(sound.offset))
            let samples = try handle.read(upToCount: sound.byteCount) ?? Data()
            player = try AVAudioPlayer(data: WAV.header(dataByteCount: samples.count) + samples)
        }
        player.prepareToPlay()
        return player
    }
}
