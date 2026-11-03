import Foundation

/// Hands the user's vocabulary to the speech-to-text model as spelling hints, for the models that take them.
///
/// OpenRouter's own `keyterms` field is turned down by every speech-to-text model it serves (Oct 2026), and its model
/// list doesn't say which take what. So the provider's own field is passed through `provider.options`, for the models
/// where it was measured to work: names the model got wrong came out right with it, and terms in the list that
/// weren't spoken never showed up (a clip with 5 rare names, and a Chinese one with 4 among 40 terms; see
/// docs/DESIGN.md). Asked directly, Mistral, Deepgram and ElevenLabs document a field of their own for it. Anything
/// else gets no hints rather than a field that might be rejected; and a request that is rejected anyway is sent again
/// without them (see APIClient).
public enum VocabularyHints {
    public enum Style: Sendable, Equatable {
        /// Azure speech (Microsoft MAI-Transcribe): `phraseList.phrases`.
        case phraseList
        /// OpenAI transcription models and Groq's Whisper: a `prompt` with the words spelled right.
        case prompt
        /// AssemblyAI Universal: `keyterms_prompt`.
        case keytermsPrompt
        /// Mistral Voxtral: a `context_bias` form field per term.
        case contextBias
        /// Deepgram Nova-3: a `keyterm` query parameter per term.
        case keyterm
        /// ElevenLabs Scribe: a `keyterms` form field per term.
        case keyterms
    }

    /// The hints of a request sent to the provider itself (not through OpenRouter).
    public enum Direct: Sendable, Equatable {
        case prompt(String)
        case contextBias([String])
        case keyterm([String])
        case keyterms([String])
    }

    /// At most this many terms are sent, the first ones in the list.
    static let maxTerms = 100
    /// Whisper reads only the last 224 tokens of a prompt, dropping the first terms of a longer one, so a prompt is kept
    /// under half of that by `TokenEstimate`. Counting characters instead let 600 Chinese characters through, far over
    /// the limit. The estimate counts one token per Chinese character and per four other characters, where Whisper's
    /// tokenizer often needs two for a Chinese character (and for a comma and space): the margin covers that. In English
    /// it is about 400 characters.
    static let maxPromptTokens = 100
    /// Deepgram limits the keyterms of a request to 500 tokens; the estimate stays well inside that.
    static let maxKeytermTokens = 400
    /// ElevenLabs takes a keyterm of at most 50 characters and five words, without these characters.
    static let maxScribeTermLength = 50
    static let scribeForbidden = Set("<>{}[]\\")

    /// How this model takes hints, or nil when it doesn't (as far as was measured or documented).
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
        case .mistral:
            return model.hasPrefix("voxtral") ? .contextBias : nil
        case .deepgram:
            // Keyterm prompting is Nova-3's only.
            return model.hasPrefix("nova-3") ? .keyterm : nil
        case .elevenlabs:
            return model.hasPrefix("scribe") ? .keyterms : nil
        case .siliconflow, .deepseek, .gemini, .anthropic, .xai, .dashscope, .zhipu, .moonshot, .volcengine, .ollama,
             .lmstudio, .custom:
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
            if TokenEstimate.count(next) > maxPromptTokens { break }
            prompt = next
        }
        return prompt
    }

    /// Deepgram's keyterms: as many of the terms, in order, as fit its token limit.
    public static func keyterms(_ terms: [String]) -> [String] {
        var total = 0
        var kept: [String] = []
        for term in terms {
            total += TokenEstimate.count(term)
            if total > maxKeytermTokens { break }
            kept.append(term)
        }
        return kept
    }

    /// The terms ElevenLabs takes: at most 50 characters and five words, none of `<>{}[]\`.
    public static func scribeTerms(_ terms: [String]) -> [String] {
        terms.filter { term in
            term.count <= maxScribeTermLength
                && term.split(whereSeparator: \.isWhitespace).count <= 5
                && !term.contains(where: scribeForbidden.contains)
        }
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
        case .contextBias, .keyterm, .keyterms: return nil
        }
        return ["options": options]
    }

    /// The hints for a request to the provider itself, or nil.
    public static func direct(provider: ProviderID, model: String, vocabulary: [String]) -> Direct? {
        let terms = terms(vocabulary)
        guard provider != .openrouter, !terms.isEmpty, let style = style(provider: provider, model: model) else { return nil }
        switch style {
        case .prompt:
            let prompt = prompt(terms)
            return prompt.isEmpty ? nil : .prompt(prompt)
        case .contextBias: return .contextBias(terms)
        case .keyterm:
            let kept = keyterms(terms)
            return kept.isEmpty ? nil : .keyterm(kept)
        case .keyterms:
            let kept = scribeTerms(terms)
            return kept.isEmpty ? nil : .keyterms(kept)
        case .phraseList, .keytermsPrompt: return nil
        }
    }

    /// The `prompt` form field for a direct OpenAI or Groq request, or nil.
    public static func multipartPrompt(provider: ProviderID, model: String, vocabulary: [String]) -> String? {
        if case let .prompt(prompt) = direct(provider: provider, model: model, vocabulary: vocabulary) { return prompt }
        return nil
    }
}
