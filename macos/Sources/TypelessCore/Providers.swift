import Foundation

/// How a provider's speech-to-text endpoint expects audio.
public enum STTFormat: String, Sendable {
    /// OpenRouter: JSON body with base64 audio under `input_audio`.
    case openRouterJSON
    /// OpenAI and every OpenAI-compatible server: multipart/form-data with a `file` part.
    case multipart
    /// Deepgram: the WAV itself as the body of `/listen`, the options in the query.
    case deepgram
    /// ElevenLabs Scribe: multipart/form-data to `/speech-to-text`, with `model_id` instead of `model`.
    case elevenLabs

    /// Where the audio is sent, under the base URL.
    public var path: String {
        switch self {
        case .openRouterJSON, .multipart: return "audio/transcriptions"
        case .deepgram: return "listen"
        case .elevenLabs: return "speech-to-text"
        }
    }
}

/// How the Providers page groups the providers it can add.
public enum ProviderGroup: String, CaseIterable, Sendable {
    /// Speech-to-text and clean-up.
    case both
    /// Clean-up only.
    case chat
    /// Speech-to-text only.
    case speech
    /// A server on this computer or the user's own.
    case local
}

/// The built-in providers. The raw values are stored in the settings and as the keys of the API keys, so they never change;
/// the order here is the order the app lists them in.
public enum ProviderID: String, CaseIterable, Identifiable, Codable, Sendable {
    case openrouter, openai, groq, siliconflow, deepseek
    case gemini, anthropic, mistral, xai, dashscope, zhipu, moonshot, volcengine
    case deepgram, elevenlabs
    case ollama, lmstudio
    case custom

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .openrouter: return "OpenRouter"
        case .openai: return "OpenAI"
        case .groq: return "Groq"
        case .siliconflow: return L("硅基流动 SiliconFlow", "SiliconFlow")
        case .deepseek: return "DeepSeek"
        case .gemini: return "Google Gemini"
        case .anthropic: return "Anthropic"
        case .mistral: return "Mistral AI"
        case .xai: return "xAI"
        case .dashscope: return L("阿里云百炼（通义千问）", "Alibaba Cloud Model Studio (Qwen)")
        case .zhipu: return L("智谱 GLM", "Zhipu GLM")
        case .moonshot: return L("月之暗面 Kimi", "Moonshot Kimi")
        case .volcengine: return L("火山方舟（豆包）", "Volcengine Ark (Doubao)")
        case .deepgram: return "Deepgram"
        case .elevenlabs: return "ElevenLabs"
        case .ollama: return "Ollama"
        case .lmstudio: return "LM Studio"
        case .custom: return L("自定义（OpenAI 兼容）", "Custom (OpenAI-compatible)")
        }
    }

    public var defaultBaseURL: String {
        switch self {
        case .openrouter: return "https://openrouter.ai/api/v1"
        case .openai: return "https://api.openai.com/v1"
        case .groq: return "https://api.groq.com/openai/v1"
        case .siliconflow: return "https://api.siliconflow.cn/v1"
        case .deepseek: return "https://api.deepseek.com/v1"
        case .gemini: return "https://generativelanguage.googleapis.com/v1beta/openai"
        case .anthropic: return "https://api.anthropic.com/v1"
        case .mistral: return "https://api.mistral.ai/v1"
        case .xai: return "https://api.x.ai/v1"
        // The Beijing region's DashScope domain, which every key from that region works with; the Singapore one is
        // https://dashscope-intl.aliyuncs.com/compatible-mode/v1.
        case .dashscope: return "https://dashscope.aliyuncs.com/compatible-mode/v1"
        case .zhipu: return "https://open.bigmodel.cn/api/paas/v4"
        case .moonshot: return "https://api.moonshot.cn/v1"
        case .volcengine: return "https://ark.cn-beijing.volces.com/api/v3"
        case .deepgram: return "https://api.deepgram.com/v1"
        case .elevenlabs: return "https://api.elevenlabs.io/v1"
        case .ollama: return "http://localhost:11434/v1"
        case .lmstudio: return "http://localhost:1234/v1"
        case .custom: return ""
        }
    }

    public var supportsSTT: Bool {
        switch self {
        case .openrouter, .openai, .groq, .siliconflow, .mistral, .zhipu, .deepgram, .elevenlabs, .ollama, .custom: return true
        case .deepseek, .gemini, .anthropic, .xai, .dashscope, .moonshot, .volcengine, .lmstudio: return false
        }
    }

    /// Deepgram and ElevenLabs only transcribe.
    public var supportsChat: Bool { self != .deepgram && self != .elevenlabs }

    /// Runs on the user's own computer (and so needs no key).
    public var isLocal: Bool { self == .ollama || self == .lmstudio }

    public var group: ProviderGroup {
        if isLocal || self == .custom { return .local }
        if supportsSTT && supportsChat { return .both }
        return supportsSTT ? .speech : .chat
    }

    public var sttFormat: STTFormat {
        switch self {
        case .openrouter: return .openRouterJSON
        case .deepgram: return .deepgram
        case .elevenlabs: return .elevenLabs
        default: return .multipart
        }
    }

    public var defaultSTTModel: String {
        switch self {
        case .openrouter: return "microsoft/mai-transcribe-2"
        case .openai: return "gpt-4o-transcribe"
        case .groq: return "whisper-large-v3-turbo"
        case .siliconflow: return "FunAudioLLM/SenseVoiceSmall"
        case .mistral: return "voxtral-mini-transcribe-3"
        case .zhipu: return "glm-asr-2512"
        case .deepgram: return "nova-3"
        case .elevenlabs: return "scribe_v2"
        default: return ""
        }
    }

    public var defaultChatModel: String {
        switch self {
        case .openrouter: return "google/gemini-3.8-flash"
        case .openai: return "gpt-5.6-luna"
        case .groq: return "openai/gpt-oss-120b"
        case .siliconflow: return "Qwen/Qwen2.5-7B-Instruct"
        case .deepseek: return "deepseek-chat"
        case .gemini: return "gemini-3.8-flash"
        case .anthropic: return "claude-haiku-5-5"
        case .mistral: return "mistral-small-latest"
        case .xai: return "grok-4.20-0309-non-reasoning"
        case .dashscope: return "qwen-flash"
        case .zhipu: return "glm-4.7-flash"
        case .moonshot: return "kimi-k2.6"
        case .volcengine: return "doubao-seed-2-1-lite-260915"
        case .deepgram, .elevenlabs, .ollama, .lmstudio, .custom: return ""
        }
    }

    /// A fast model from a different vendor, so a slow or failing primary rarely drags it down too.
    /// Empty where the user has to pick one.
    public var defaultBackupChatModel: String {
        switch self {
        case .openrouter: return "deepseek/deepseek-v4.1-flash"
        case .gemini: return "gemini-3.5-flash-lite"
        case .anthropic, .mistral, .xai, .dashscope, .zhipu, .moonshot, .volcengine: return defaultChatModel
        default: return ""
        }
    }

    /// A speech-to-text model from another vendor, asked when the main one is late or fails. Empty where the user
    /// has to pick one.
    public var defaultBackupSTTModel: String {
        switch self {
        case .openrouter: return "openai/gpt-4o-mini-transcribe"
        case .mistral, .zhipu, .deepgram, .elevenlabs: return defaultSTTModel
        default: return ""
        }
    }

    public var keyPlaceholder: String {
        switch self {
        case .openrouter: return "sk-or-v1-…"
        case .openai, .siliconflow, .deepseek, .dashscope, .moonshot: return "sk-…"
        case .groq: return "gsk_…"
        case .gemini: return "AIza…"
        case .anthropic: return "sk-ant-…"
        case .xai: return "xai-…"
        case .elevenlabs: return "sk_…"
        case .mistral, .zhipu, .volcengine, .deepgram: return L("粘贴 API Key", "Paste your API key")
        case .ollama, .lmstudio, .custom: return L("可留空", "optional")
        }
    }

    /// Where to create an API key.
    public var keysURL: URL? {
        switch self {
        case .openrouter: return URL(string: "https://openrouter.ai/keys")
        case .openai: return URL(string: "https://platform.openai.com/api-keys")
        case .groq: return URL(string: "https://console.groq.com/keys")
        case .siliconflow: return URL(string: "https://cloud.siliconflow.cn/account/ak")
        case .deepseek: return URL(string: "https://platform.deepseek.com/api_keys")
        case .gemini: return URL(string: "https://aistudio.google.com/apikey")
        case .anthropic: return URL(string: "https://platform.claude.com/settings/keys")
        case .mistral: return URL(string: "https://console.mistral.ai/api-keys")
        case .xai: return URL(string: "https://console.x.ai")
        case .dashscope: return URL(string: "https://bailian.console.aliyun.com/?tab=model#/api-key")
        case .zhipu: return URL(string: "https://open.bigmodel.cn/usercenter/proj-mgmt/apikeys")
        case .moonshot: return URL(string: "https://platform.moonshot.cn/console/api-keys")
        case .volcengine: return URL(string: "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey")
        case .deepgram: return URL(string: "https://console.deepgram.com")
        case .elevenlabs: return URL(string: "https://elevenlabs.io/app/settings/api-keys")
        case .ollama, .lmstudio, .custom: return nil
        }
    }

    /// Local servers (e.g. a self-hosted whisper, Ollama, LM Studio) often need no key.
    public var requiresKey: Bool { !isLocal && self != .custom }

    /// The header a request carries the key in: a bearer token, except Deepgram's `Token` scheme and ElevenLabs'
    /// own header.
    public func authorization(_ key: String) -> (field: String, value: String) {
        switch self {
        case .deepgram: return ("Authorization", "Token \(key)")
        case .elevenlabs: return ("xi-api-key", key)
        default: return ("Authorization", "Bearer \(key)")
        }
    }

    /// The headers of a GET for the model list or the key check. Anthropic's OpenAI-compatible layer takes a bearer
    /// token for chat; its model list is the native API's, which takes `x-api-key`.
    public func listHeaders(_ key: String) -> [(field: String, value: String)] {
        guard !key.isEmpty else { return [] }
        if self == .anthropic { return [("x-api-key", key), ("anthropic-version", "2023-06-01")] }
        return [authorization(key)]
    }

    /// Whether `GET /models` lists the provider's models. Deepgram and ElevenLabs have no such list for speech models.
    public var listsModels: Bool { self != .deepgram && self != .elevenlabs }

    /// What the Test button asks for to check a key, under the base URL: OpenRouter's key endpoint (its model list is
    /// public), Deepgram's projects, ElevenLabs' user; for every other provider the model list.
    public var keyCheckPath: String {
        switch self {
        case .openrouter: return "key"
        case .deepgram: return "projects"
        case .elevenlabs: return "user"
        default: return "models"
        }
    }

    /// Volcengine Ark documents no model list: a 404 from it still means the server answered and took the key.
    public var modelListMayBeMissing: Bool { self == .volcengine }

    // MARK: Clean-up requests

    /// Whether a clean-up sends `temperature` (0.2). Not to OpenAI (its reasoning models reject it), Anthropic (Claude 4.7
    /// and later reject any value under 1), Gemini (Google asks to leave Gemini 3 at its default) or Kimi (its thinking
    /// models fix it); OpenRouter decides by the model's metadata.
    public var chatSendsTemperature: Bool {
        switch self {
        case .openai, .anthropic, .gemini, .moonshot: return false
        default: return true
        }
    }

    /// Whether a clean-up asks for token counts on the stream (`stream_options.include_usage`). OpenRouter always sends
    /// them; Mistral sends them anyway and turns down fields it doesn't know.
    public var chatSendsStreamOptions: Bool { self != .openrouter && self != .mistral }

    /// Anthropic's API needs `max_tokens` on every request, the bare one sent again without the optional fields too.
    public var chatNeedsMaxTokens: Bool { self == .anthropic }

    /// Whether the clean-up switches the model's reasoning off or to its minimum (see `ReasoningConfig.direct`).
    public var minimisesReasoning: Bool {
        switch self {
        case .openrouter, .gemini, .anthropic, .mistral, .xai, .dashscope, .zhipu, .moonshot, .volcengine, .ollama: return true
        case .openai, .groq, .siliconflow, .deepseek, .deepgram, .elevenlabs, .lmstudio, .custom: return false
        }
    }
}

public struct ProviderEndpoint: Sendable, Equatable {
    public var id: ProviderID
    public var baseURL: URL
    public var apiKey: String

    public init(id: ProviderID, baseURL: URL, apiKey: String) {
        self.id = id
        self.baseURL = baseURL
        self.apiKey = apiKey
    }

    /// Convenience used by tests and the CLI.
    public static func openRouter(apiKey: String) -> ProviderEndpoint {
        ProviderEndpoint(id: .openrouter, baseURL: URL(string: ProviderID.openrouter.defaultBaseURL)!, apiKey: apiKey)
    }

    /// The provider at its default base URL, for tests.
    public static func preset(_ id: ProviderID, apiKey: String) -> ProviderEndpoint {
        ProviderEndpoint(id: id, baseURL: URL(string: id.defaultBaseURL.isEmpty ? "http://localhost:8000/v1" : id.defaultBaseURL)!,
                         apiKey: apiKey)
    }
}
