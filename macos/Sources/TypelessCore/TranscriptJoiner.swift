import Foundation

public enum TranscriptJoiner {
    /// Joins per-chunk transcripts. Chinese and Japanese text is concatenated directly; elsewhere (Latin letters,
    /// digits, Korean, which puts spaces between words) a space goes in, so "hello" + "world" doesn't become
    /// "helloworld".
    public static func join(_ parts: [String]) -> String {
        var result = ""
        for raw in parts {
            let part = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !part.isEmpty else { continue }
            if let last = result.unicodeScalars.last, let first = part.unicodeScalars.first,
               needsSpace(between: last, and: first) {
                result += " "
            }
            result += part
        }
        return result
    }

    private static func needsSpace(between a: Unicode.Scalar, and b: Unicode.Scalar) -> Bool {
        if joinsWithoutSpace(a) || joinsWithoutSpace(b) { return false }
        if CharacterSet.whitespacesAndNewlines.contains(a) { return false }
        // After sentence punctuation like "." or "," a space reads naturally in Latin text.
        if CharacterSet.punctuationCharacters.contains(b) { return false }
        return true
    }

    /// Script written without spaces between words: Han, kana, CJK punctuation and full-width forms. Not Hangul, unlike
    /// `isCJK`: Korean separates its words with spaces, so two Korean chunks need one between them.
    static func joinsWithoutSpace(_ s: Unicode.Scalar) -> Bool {
        isCJK(s) && !(0xAC00...0xD7AF).contains(s.value)
    }

    static func isCJK(_ s: Unicode.Scalar) -> Bool {
        switch s.value {
        case 0x3000...0x303F, // CJK punctuation
             0x3040...0x30FF, // Kana
             0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, // Han
             0xAC00...0xD7AF, // Hangul
             0xFF00...0xFFEF: // Full-width forms
            return true
        default:
            return false
        }
    }
}
