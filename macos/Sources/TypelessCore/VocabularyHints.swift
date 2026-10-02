import Foundation

/// Hands the user's vocabulary to the speech-to-text model as spelling hints, for the models that take them.
///
/// OpenRouter's own `keyterms` field is turned down by every speech-to-text model it serves (Oct 2026), and its model
/// list doesn't say which take what. So the provider's own field is passed through `provider.options`, for the models
/// where it was measured to work: names the model got wrong came out right with it, and terms in the list that
/// weren't spoken never showed up (a clip with 5 rare names, and a Chinese one with 4 among 40 terms; see
/// docs/DESIGN.md). Anything else gets no hints rather than a field that might be rejected; and a request that is
/// rejected anyway is sent again without them (see APIClient).
public enum VocabularyHints {
    public enum Style: Sendable, Equatable {
        /// Azure speech (Microsoft MAI-Transcribe): `phraseList.phrases`.
        case phraseList
        /// OpenAI transcription models and Groq's Whisper: a `prompt` with the words spelled right.
        case prompt
        /// AssemblyAI Universal: `keyterms_prompt`.
        case keytermsPrompt
    }

    /// At most this many terms are sent, the first ones in the list.
    static let maxTerms = 100
    /// Whisper reads only the last 224 tokens of a prompt, so a prompt is kept well under that.
    static let maxPromptCharacters = 600

    /// How this model takes hints, or nil when it doesn't (as far as was measured).
    public static func style(provider: ProviderID, model: String) -> Style? {
        let model = model.trimmingCharacters(in: .whitespaces).lowercased()
        switch provider {
        case .openrouter:
            if model.hasPrefix("microsoft/mai-transcribe") { return .phraseList }
            if model.hasPrefix("assemblyai/") { return .keytermsPrompt }
            if ["openai/gpt-4o-transcribe", "openai/gpt-4o-mini-transcribe", "openai/gpt-transcribe", "openai/whisper-1"]
                .contains(where: model.hasPrefix) { return .prompt }
            return nil
        case .openai, .groq:
            // Both document `prompt` on /audio/transcriptions.
            return .prompt
        case .siliconflow, .deepseek, .custom:
            return nil
        }
    }

    /// The terms to send: trimmed, without duplicates, at most `maxTerms`.
    public static func terms(_ vocabulary: [String]) -> [String] {
        var seen = Set<String>()
        return vocabulary
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
            .prefix(maxTerms)
            .map { $0 }
    }

    /// The terms as a prompt: just the words, comma-separated, cut at a whole term.
    public static func prompt(_ terms: [String]) -> String {
        var prompt = ""
        for term in terms {
            let next = prompt.isEmpty ? term : prompt + ", " + term
            if next.count > maxPromptCharacters { break }
            prompt = next
        }
        return prompt
    }

    /// The OpenRouter `provider` object carrying the hints for this model, or nil.
    public static func openRouterProvider(model: String, vocabulary: [String]) -> [String: Any]? {
        let terms = terms(vocabulary)
        guard !terms.isEmpty, let style = style(provider: .openrouter, model: model) else { return nil }
        let options: [String: Any]
        switch style {
        case .phraseList: options = ["azure": ["phraseList": ["phrases": terms]]]
        case .prompt: options = ["openai": ["prompt": prompt(terms)]]
        case .keytermsPrompt: options = ["assemblyai": ["keyterms_prompt": terms]]
        }
        return ["options": options]
    }

    /// The `prompt` form field for a direct OpenAI or Groq request, or nil.
    public static func multipartPrompt(provider: ProviderID, model: String, vocabulary: [String]) -> String? {
        let terms = terms(vocabulary)
        guard provider != .openrouter, !terms.isEmpty, style(provider: provider, model: model) == .prompt else { return nil }
        return prompt(terms)
    }
}
