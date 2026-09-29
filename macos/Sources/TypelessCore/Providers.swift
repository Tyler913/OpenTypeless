import Foundation

/// How a provider's `/audio/transcriptions` endpoint expects audio.
public enum STTFormat: String, Sendable {
    /// OpenRouter: JSON body with base64 audio under `input_audio`.
    case openRouterJSON
    /// OpenAI and every OpenAI-compatible server: multipart/form-data with a `file` part.
    case multipart
}

public enum ProviderID: String, CaseIterable, Identifiable, Codable, Sendable {
    case openrouter, openai, groq, siliconflow, deepseek, custom

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .openrouter: return "OpenRouter"
        case .openai: return "OpenAI"
        case .groq: return "Groq"
        case .siliconflow: return L("硅基流动 SiliconFlow", "SiliconFlow")
        case .deepseek: return "DeepSeek"
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
        case .custom: return ""
        }
    }

    public var supportsSTT: Bool { self != .deepseek }
    public var supportsChat: Bool { true }

    public var sttFormat: STTFormat { self == .openrouter ? .openRouterJSON : .multipart }

    public var defaultSTTModel: String {
        switch self {
        case .openrouter: return "microsoft/mai-transcribe-2"
        case .openai: return "gpt-4o-transcribe"
        case .groq: return "whisper-large-v3-turbo"
        case .siliconflow: return "FunAudioLLM/SenseVoiceSmall"
        case .deepseek, .custom: return ""
        }
    }

    public var defaultChatModel: String {
        switch self {
        case .openrouter: return "google/gemini-3.8-flash"
        case .openai: return "gpt-5.6-luna"
        case .groq: return "openai/gpt-oss-120b"
        case .siliconflow: return "Qwen/Qwen2.5-7B-Instruct"
        case .deepseek: return "deepseek-chat"
        case .custom: return ""
        }
    }

    /// A fast model from a different vendor, so a slow or failing primary rarely drags it down too.
    /// Empty where the user has to pick one.
    public var defaultBackupChatModel: String {
        switch self {
        case .openrouter: return "deepseek/deepseek-v4.1-flash"
        default: return ""
        }
    }

    /// A speech-to-text model from another vendor, asked when the main one is late or fails. Empty where the user
    /// has to pick one.
    public var defaultBackupSTTModel: String {
        switch self {
        case .openrouter: return "openai/gpt-4o-mini-transcribe"
        default: return ""
        }
    }

    public var keyPlaceholder: String {
        switch self {
        case .openrouter: return "sk-or-v1-…"
        case .openai, .siliconflow, .deepseek: return "sk-…"
        case .groq: return "gsk_…"
        case .custom: return L("可留空", "optional")
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
        case .custom: return nil
        }
    }

    /// Local servers (e.g. a self-hosted whisper) often need no key.
    public var requiresKey: Bool { self != .custom }
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
}
