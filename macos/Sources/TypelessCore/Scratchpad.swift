import Foundation

/// A note in the Scratchpad: plain text, when it was made and when it was last changed.
public struct ScratchpadNote: Equatable, Sendable, Identifiable {
    public var id: String
    public var text: String
    public var created: Date
    public var modified: Date
    /// The local day ("2026-11-07") whose dictations that had nowhere to go are added to this note (see
    /// `Scratchpad.capture`); nil for a note the user started.
    public var captureDay: String?
    /// When it was deleted. A deleted note is never listed; it's kept a while (see `Scratchpad.keepDeleted`) so Undo can
    /// bring it back.
    public var deleted: Date?

    public init(id: String, text: String, created: Date, modified: Date, captureDay: String? = nil, deleted: Date? = nil) {
        self.id = id
        self.text = text
        self.created = created
        self.modified = modified
        self.captureDay = captureDay
        self.deleted = deleted
    }

    /// Its first line with text (see `Scratchpad.title(of:)`); "" for an empty note.
    public var title: String { Scratchpad.title(of: text) }
    /// Nothing but spaces and line breaks in it.
    public var isBlank: Bool { Scratchpad.title(of: text).isEmpty }
}

/// The Scratchpad's notes, as `scratchpad.json` in the data folder keeps them: plain text, listed by when they were last
/// edited. Times are whole seconds (as the file stores them), so a note is listed in the same place after a relaunch.
/// The Windows app ports this line by line; `testdata/scratchpad-cases.json` holds cases both test suites read.
public struct Scratchpad: Equatable, Sendable {
    /// Every note, deleted ones included, in the order the file keeps them (a new note last).
    public private(set) var entries: [ScratchpadNote]
    /// The file is there but couldn't be read (or a damaged one couldn't be moved aside): the notes standing in for it
    /// this session are never saved over it.
    public var isUnreadable = false

    /// How long a deleted note is kept for Undo.
    public static let keepDeleted: TimeInterval = 30 * 24 * 3600
    /// The most characters a title shows; the list cuts it shorter to fit.
    public static let titleLength = 80

    public init(entries: [ScratchpadNote] = []) {
        self.entries = entries
    }

    /// The notes to list: not deleted, the last edited first (then the last made, then the one further down the file).
    public var notes: [ScratchpadNote] {
        entries.enumerated()
            .filter { $0.element.deleted == nil }
            .sorted { a, b in
                if a.element.modified != b.element.modified { return a.element.modified > b.element.modified }
                if a.element.created != b.element.created { return a.element.created > b.element.created }
                return a.offset > b.offset
            }
            .map(\.element)
    }

    public func note(_ id: String) -> ScratchpadNote? {
        entries.first { $0.id == id }
    }

    /// A new, empty note, made `now`.
    @discardableResult
    public mutating func add(now: Date = Date(), id: String = UUID().uuidString) -> ScratchpadNote {
        let stamp = Self.stamp(now)
        let note = ScratchpadNote(id: id, text: "", created: stamp, modified: stamp)
        entries.append(note)
        return note
    }

    /// The note's text as the user edited it; it counts as edited `now` only when the text changed.
    public mutating func setText(_ text: String, of id: String, now: Date = Date()) {
        guard let index = entries.firstIndex(where: { $0.id == id }), entries[index].text != text else { return }
        entries[index].text = text
        entries[index].modified = Self.stamp(now)
    }

    /// Takes the note off the list; `restore` brings it back.
    public mutating func delete(_ id: String, now: Date = Date()) {
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return }
        entries[index].deleted = Self.stamp(now)
    }

    public mutating func restore(_ id: String) {
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return }
        entries[index].deleted = nil
    }

    /// Drops the notes left empty (a new note never typed in), but `id`'s, the one being edited.
    public mutating func removeBlank(except id: String? = nil) {
        entries.removeAll { $0.isBlank && $0.id != id }
    }

    /// A dictation that had nowhere to go: added to the end of the note for `day` (the local date, "2026-11-07"), or a
    /// new note for that day when there's none (or it was deleted). Returns the note's ID.
    @discardableResult
    public mutating func capture(_ text: String, day: String, now: Date = Date(), newID: String = UUID().uuidString) -> String {
        let stamp = Self.stamp(now)
        if let index = entries.lastIndex(where: { $0.captureDay == day && $0.deleted == nil }) {
            entries[index].text = Self.appending(text, to: entries[index].text)
            entries[index].modified = stamp
            return entries[index].id
        }
        entries.append(ScratchpadNote(id: newID, text: text, created: stamp, modified: stamp, captureDay: day))
        return newID
    }

    /// Forgets the notes deleted longer ago than `keepDeleted`.
    public mutating func purge(now: Date = Date()) {
        entries.removeAll { note in note.deleted.map { now.timeIntervalSince($0) > Self.keepDeleted } ?? false }
    }

    /// The first line that has text, without the spaces around it, cut to `titleLength` characters; "" when there's none.
    public static func title(of text: String) -> String {
        for line in text.split(whereSeparator: \.isNewline) {
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty { return String(trimmed.prefix(titleLength)) }
        }
        return ""
    }

    /// `addition` after `existing`, a blank line between them; the spaces and line breaks `existing` ends with give way
    /// to it. An empty note just gets `addition`.
    public static func appending(_ addition: String, to existing: String) -> String {
        let scalars = existing.unicodeScalars
        var end = scalars.endIndex
        while end > scalars.startIndex, trailingBlanks.contains(scalars[scalars.index(before: end)]) {
            end = scalars.index(before: end)
        }
        let head = String(scalars[..<end])
        return head.isEmpty ? addition : head + "\n\n" + addition
    }

    private static let trailingBlanks: Set<Unicode.Scalar> = [" ", "\t", "\n", "\r"]

    /// `date` cut to whole seconds, as the file keeps it.
    static func stamp(_ date: Date) -> Date {
        Date(timeIntervalSince1970: date.timeIntervalSince1970.rounded(.down))
    }

    // MARK: - Storage

    /// The notes in a saved file. Each one is read on its own: an entry without text is skipped, one without an ID (or
    /// with one already taken) gets a new one, a time that can't be read falls back to the note's other time or `now`,
    /// and fields this version doesn't know are ignored. Nil when the data isn't a notes file at all.
    public static func decode(_ data: Data, now: Date = Date()) -> Scratchpad? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let items = object["notes"] as? [Any] else { return nil }
        var notes: [ScratchpadNote] = []
        var ids = Set<String>()
        for case let item as [String: Any] in items {
            guard let text = item["text"] as? String else { continue }
            var id = (item["id"] as? String) ?? ""
            if id.isEmpty || ids.contains(id) { id = UUID().uuidString }
            ids.insert(id)
            let created = (item["created"] as? String).flatMap(parseDate)
            let modified = (item["modified"] as? String).flatMap(parseDate)
            let captureDay = (item["captureDay"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            notes.append(ScratchpadNote(id: id, text: text, created: created ?? modified ?? stamp(now),
                                        modified: modified ?? created ?? stamp(now), captureDay: captureDay,
                                        deleted: (item["deleted"] as? String).flatMap(parseDate)))
        }
        return Scratchpad(entries: notes)
    }

    /// The file's contents. Empty notes aren't kept.
    public func jsonData() -> Data? {
        let notes: [[String: Any]] = entries.filter { !$0.isBlank }.map { note in
            var item: [String: Any] = ["id": note.id, "text": note.text,
                                       "created": Self.formatDate(note.created), "modified": Self.formatDate(note.modified)]
            if let day = note.captureDay { item["captureDay"] = day }
            if let deleted = note.deleted { item["deleted"] = Self.formatDate(deleted) }
            return item
        }
        return try? JSONSerialization.data(withJSONObject: ["version": 1, "notes": notes] as [String: Any],
                                           options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    }

    /// The saved notes, without those deleted too long ago; none when there's no file yet. A file that isn't a notes file
    /// is moved aside (see SafeFile) before an empty Scratchpad takes its place, so the next save doesn't overwrite it.
    public static func load(from url: URL, now: Date = Date()) -> Scratchpad {
        guard FileManager.default.fileExists(atPath: url.path) else { return Scratchpad() }
        // One that can't be read at all isn't damaged: it stays, and the notes standing in for it aren't saved.
        guard let data = try? Data(contentsOf: url) else {
            var notes = Scratchpad()
            notes.isUnreadable = true
            return notes
        }
        if var notes = decode(data, now: now) {
            notes.purge(now: now)
            return notes
        }
        var notes = Scratchpad()
        // Not moved aside (the folder is read-only, say): then it's the only copy, and isn't saved over either.
        if SafeFile.moveAside(url) == nil { notes.isUnreadable = true }
        return notes
    }

    /// Writes the file whole (atomically). False when it wasn't written.
    @discardableResult
    public func save(to url: URL) -> Bool {
        guard !isUnreadable, let data = jsonData() else { return false }
        return (try? data.write(to: url, options: .atomic)) != nil
    }

    /// "2026-11-07T09:30:00Z", as History's files write their dates.
    static func formatDate(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.string(from: date)
    }

    /// ISO 8601, with or without fractions of a second (kept to whole seconds).
    static func parseDate(_ text: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        if let date = formatter.date(from: text) { return date }
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: text).map(stamp)
    }
}

/// "Save to Scratchpad when there's nowhere to type": whether a dictation's text, once delivered, is also added to the
/// Scratchpad (see `Scratchpad.capture`).
public enum ScratchpadCapture {
    /// Where a dictation's text ended up.
    public enum Outcome: String, CaseIterable, Sendable {
        /// Pasted at the cursor.
        case pasted
        /// Typed into the Scratchpad's own editor.
        case scratchpad
        /// Focus was on something that takes no text: the clipboard.
        case notEditable
        /// Another app (or window) came to the front, or one with other settings: the clipboard.
        case movedAway
        /// The paste was sent but nothing took it: the clipboard.
        case notPasted
        /// No permission to paste: the clipboard.
        case noPermission
        /// The screen locked while it was processed: the clipboard.
        case locked
        /// Re-transcribed from History, which copies it and keeps it: the clipboard.
        case fromHistory
        /// Esc, or another dictation took over: nowhere.
        case cancelled
    }

    /// Only text that went to the clipboard because there was nowhere to put it, while the setting is on.
    public static func saves(_ text: String, after outcome: Outcome, enabled: Bool) -> Bool {
        guard enabled, !Scratchpad.title(of: text).isEmpty else { return false }
        switch outcome {
        case .notEditable, .movedAway, .notPasted, .noPermission, .locked: return true
        case .pasted, .scratchpad, .fromHistory, .cancelled: return false
        }
    }
}
