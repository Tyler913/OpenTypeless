namespace TypelessCore;

/// <summary>How a provider's speech-to-text endpoint expects audio.</summary>
public enum SttFormat
{
    /// <summary>OpenRouter: JSON body with base64 audio under <c>input_audio</c>.</summary>
    OpenRouterJson,
    /// <summary>OpenAI and every OpenAI-compatible server: multipart/form-data with a <c>file</c> part.</summary>
    Multipart,
    /// <summary>Deepgram: the WAV itself as the body of <c>/listen</c>, the options in the query.</summary>
    Deepgram,
    /// <summary>ElevenLabs Scribe: multipart/form-data to <c>/speech-to-text</c>, with <c>model_id</c> instead of <c>model</c>.</summary>
    ElevenLabs,
}

/// <summary>How the Providers page groups the providers it can add.</summary>
public enum ProviderGroup
{
    /// <summary>Speech-to-text and clean-up.</summary>
    Both,
    /// <summary>Clean-up only.</summary>
    Chat,
    /// <summary>Speech-to-text only.</summary>
    Speech,
    /// <summary>A server on this computer or the user's own.</summary>
    Local,
}

/// <summary>
/// The built-in providers. Stored by <see cref="ProviderIdExtensions.RawValue"/> (the settings and the API keys use it), so
/// new members only ever go at the end; <see cref="ProviderIdExtensions.All"/> gives the order the app lists them in.
/// </summary>
public enum ProviderId
{
    OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek, Custom,
    Gemini, Anthropic, Mistral, XAI, DashScope, Zhipu, Moonshot, Volcengine,
    Deepgram, ElevenLabs,
    Ollama, LMStudio,
}

public static class ProviderIdExtensions
{
    public static readonly ProviderId[] All =
    [
        ProviderId.OpenRouter, ProviderId.OpenAI, ProviderId.Groq, ProviderId.SiliconFlow, ProviderId.DeepSeek,
        ProviderId.Gemini, ProviderId.Anthropic, ProviderId.Mistral, ProviderId.XAI, ProviderId.DashScope, ProviderId.Zhipu,
        ProviderId.Moonshot, ProviderId.Volcengine,
        ProviderId.Deepgram, ProviderId.ElevenLabs,
        ProviderId.Ollama, ProviderId.LMStudio,
        ProviderId.Custom,
    ];

    public static string RawValue(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "openrouter",
        ProviderId.OpenAI => "openai",
        ProviderId.Groq => "groq",
        ProviderId.SiliconFlow => "siliconflow",
        ProviderId.DeepSeek => "deepseek",
        ProviderId.Gemini => "gemini",
        ProviderId.Anthropic => "anthropic",
        ProviderId.Mistral => "mistral",
        ProviderId.XAI => "xai",
        ProviderId.DashScope => "dashscope",
        ProviderId.Zhipu => "zhipu",
        ProviderId.Moonshot => "moonshot",
        ProviderId.Volcengine => "volcengine",
        ProviderId.Deepgram => "deepgram",
        ProviderId.ElevenLabs => "elevenlabs",
        ProviderId.Ollama => "ollama",
        ProviderId.LMStudio => "lmstudio",
        _ => "custom",
    };

    public static ProviderId? Parse(string? raw) => All.Cast<ProviderId?>().FirstOrDefault(p => p!.Value.RawValue() == raw);

    public static string DisplayName(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "OpenRouter",
        ProviderId.OpenAI => "OpenAI",
        ProviderId.Groq => "Groq",
        ProviderId.SiliconFlow => L("硅基流动 SiliconFlow", "SiliconFlow"),
        ProviderId.DeepSeek => "DeepSeek",
        ProviderId.Gemini => "Google Gemini",
        ProviderId.Anthropic => "Anthropic",
        ProviderId.Mistral => "Mistral AI",
        ProviderId.XAI => "xAI",
        ProviderId.DashScope => L("阿里云百炼（通义千问）", "Alibaba Cloud Model Studio (Qwen)"),
        ProviderId.Zhipu => L("智谱 GLM", "Zhipu GLM"),
        ProviderId.Moonshot => L("月之暗面 Kimi", "Moonshot Kimi"),
        ProviderId.Volcengine => L("火山方舟（豆包）", "Volcengine Ark (Doubao)"),
        ProviderId.Deepgram => "Deepgram",
        ProviderId.ElevenLabs => "ElevenLabs",
        ProviderId.Ollama => "Ollama",
        ProviderId.LMStudio => "LM Studio",
        _ => L("自定义（OpenAI 兼容）", "Custom (OpenAI-compatible)"),
    };

    public static string DefaultBaseUrl(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "https://openrouter.ai/api/v1",
        ProviderId.OpenAI => "https://api.openai.com/v1",
        ProviderId.Groq => "https://api.groq.com/openai/v1",
        ProviderId.SiliconFlow => "https://api.siliconflow.cn/v1",
        ProviderId.DeepSeek => "https://api.deepseek.com/v1",
        ProviderId.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        ProviderId.Anthropic => "https://api.anthropic.com/v1",
        ProviderId.Mistral => "https://api.mistral.ai/v1",
        ProviderId.XAI => "https://api.x.ai/v1",
        // The Beijing region's DashScope domain, which every key from that region works with; the Singapore one is
        // https://dashscope-intl.aliyuncs.com/compatible-mode/v1.
        ProviderId.DashScope => "https://dashscope.aliyuncs.com/compatible-mode/v1",
        ProviderId.Zhipu => "https://open.bigmodel.cn/api/paas/v4",
        ProviderId.Moonshot => "https://api.moonshot.cn/v1",
        ProviderId.Volcengine => "https://ark.cn-beijing.volces.com/api/v3",
        ProviderId.Deepgram => "https://api.deepgram.com/v1",
        ProviderId.ElevenLabs => "https://api.elevenlabs.io/v1",
        ProviderId.Ollama => "http://localhost:11434/v1",
        ProviderId.LMStudio => "http://localhost:1234/v1",
        _ => "",
    };

    public static bool SupportsStt(this ProviderId id) => id is ProviderId.OpenRouter or ProviderId.OpenAI or ProviderId.Groq
        or ProviderId.SiliconFlow or ProviderId.Mistral or ProviderId.Zhipu or ProviderId.Deepgram or ProviderId.ElevenLabs
        or ProviderId.Ollama or ProviderId.Custom;

    /// <summary>Deepgram and ElevenLabs only transcribe.</summary>
    public static bool SupportsChat(this ProviderId id) => id is not (ProviderId.Deepgram or ProviderId.ElevenLabs);

    /// <summary>Runs on the user's own computer (and so needs no key).</summary>
    public static bool IsLocal(this ProviderId id) => id is ProviderId.Ollama or ProviderId.LMStudio;

    public static ProviderGroup Group(this ProviderId id)
    {
        if (id.IsLocal() || id == ProviderId.Custom) return ProviderGroup.Local;
        if (id.SupportsStt() && id.SupportsChat()) return ProviderGroup.Both;
        return id.SupportsStt() ? ProviderGroup.Speech : ProviderGroup.Chat;
    }

    public static SttFormat SttFormat(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => TypelessCore.SttFormat.OpenRouterJson,
        ProviderId.Deepgram => TypelessCore.SttFormat.Deepgram,
        ProviderId.ElevenLabs => TypelessCore.SttFormat.ElevenLabs,
        _ => TypelessCore.SttFormat.Multipart,
    };

    /// <summary>Where the audio is sent, under the base URL.</summary>
    public static string RequestPath(this SttFormat format) => format switch
    {
        TypelessCore.SttFormat.Deepgram => "listen",
        TypelessCore.SttFormat.ElevenLabs => "speech-to-text",
        _ => "audio/transcriptions",
    };

    public static string DefaultSttModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "microsoft/mai-transcribe-2",
        ProviderId.OpenAI => "gpt-4o-transcribe",
        ProviderId.Groq => "whisper-large-v3-turbo",
        ProviderId.SiliconFlow => "FunAudioLLM/SenseVoiceSmall",
        ProviderId.Mistral => "voxtral-mini-transcribe-3",
        ProviderId.Zhipu => "glm-asr-2512",
        ProviderId.Deepgram => "nova-3",
        ProviderId.ElevenLabs => "scribe_v2",
        _ => "",
    };

    public static string DefaultChatModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "google/gemini-3.8-flash",
        ProviderId.OpenAI => "gpt-5.6-luna",
        ProviderId.Groq => "openai/gpt-oss-120b",
        ProviderId.SiliconFlow => "Qwen/Qwen2.5-7B-Instruct",
        ProviderId.DeepSeek => "deepseek-chat",
        ProviderId.Gemini => "gemini-3.8-flash",
        ProviderId.Anthropic => "claude-haiku-5-5",
        ProviderId.Mistral => "mistral-small-latest",
        ProviderId.XAI => "grok-4.20-0309-non-reasoning",
        ProviderId.DashScope => "qwen-flash",
        ProviderId.Zhipu => "glm-4.7-flash",
        ProviderId.Moonshot => "kimi-k2.6",
        ProviderId.Volcengine => "doubao-seed-2-1-lite-260915",
        _ => "",
    };

    /// <summary>
    /// A fast model from a different vendor, so a slow or failing primary rarely drags it down too.
    /// Empty where the user has to pick one.
    /// </summary>
    public static string DefaultBackupChatModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "deepseek/deepseek-v4.1-flash",
        ProviderId.Gemini => "gemini-3.5-flash-lite",
        ProviderId.Anthropic or ProviderId.Mistral or ProviderId.XAI or ProviderId.DashScope or ProviderId.Zhipu
            or ProviderId.Moonshot or ProviderId.Volcengine => id.DefaultChatModel(),
        _ => "",
    };

    /// <summary>A speech-to-text model from another vendor, asked when the main one is late or fails. Empty where the user has to pick one.</summary>
    public static string DefaultBackupSttModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "openai/gpt-4o-mini-transcribe",
        ProviderId.Mistral or ProviderId.Zhipu or ProviderId.Deepgram or ProviderId.ElevenLabs => id.DefaultSttModel(),
        _ => "",
    };

    public static string KeyPlaceholder(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "sk-or-v1-…",
        ProviderId.OpenAI or ProviderId.SiliconFlow or ProviderId.DeepSeek or ProviderId.DashScope or ProviderId.Moonshot => "sk-…",
        ProviderId.Groq => "gsk_…",
        ProviderId.Gemini => "AIza…",
        ProviderId.Anthropic => "sk-ant-…",
        ProviderId.XAI => "xai-…",
        ProviderId.ElevenLabs => "sk_…",
        ProviderId.Mistral or ProviderId.Zhipu or ProviderId.Volcengine or ProviderId.Deepgram => L("粘贴 API Key", "Paste your API key"),
        _ => L("可留空", "optional"),
    };

    /// <summary>Where to create an API key.</summary>
    public static string? KeysUrl(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "https://openrouter.ai/keys",
        ProviderId.OpenAI => "https://platform.openai.com/api-keys",
        ProviderId.Groq => "https://console.groq.com/keys",
        ProviderId.SiliconFlow => "https://cloud.siliconflow.cn/account/ak",
        ProviderId.DeepSeek => "https://platform.deepseek.com/api_keys",
        ProviderId.Gemini => "https://aistudio.google.com/apikey",
        ProviderId.Anthropic => "https://platform.claude.com/settings/keys",
        ProviderId.Mistral => "https://console.mistral.ai/api-keys",
        ProviderId.XAI => "https://console.x.ai",
        ProviderId.DashScope => "https://bailian.console.aliyun.com/?tab=model#/api-key",
        ProviderId.Zhipu => "https://open.bigmodel.cn/usercenter/proj-mgmt/apikeys",
        ProviderId.Moonshot => "https://platform.moonshot.cn/console/api-keys",
        ProviderId.Volcengine => "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey",
        ProviderId.Deepgram => "https://console.deepgram.com",
        ProviderId.ElevenLabs => "https://elevenlabs.io/app/settings/api-keys",
        _ => null,
    };

    /// <summary>Local servers (e.g. a self-hosted whisper, Ollama, LM Studio) often need no key.</summary>
    public static bool RequiresKey(this ProviderId id) => !id.IsLocal() && id != ProviderId.Custom;

    /// <summary>
    /// The header a request carries the key in: a bearer token, except Deepgram's <c>Token</c> scheme and ElevenLabs' own
    /// header.
    /// </summary>
    public static (string Field, string Value) Authorization(this ProviderId id, string key) => id switch
    {
        ProviderId.Deepgram => ("Authorization", "Token " + key),
        ProviderId.ElevenLabs => ("xi-api-key", key),
        _ => ("Authorization", "Bearer " + key),
    };

    /// <summary>
    /// The headers of a GET for the model list or the key check. Anthropic's OpenAI-compatible layer takes a bearer token
    /// for chat; its model list is the native API's, which takes <c>x-api-key</c>.
    /// </summary>
    public static (string Field, string Value)[] ListHeaders(this ProviderId id, string key)
    {
        if (key.Length == 0) return [];
        if (id == ProviderId.Anthropic) return [("x-api-key", key), ("anthropic-version", "2023-06-01")];
        return [id.Authorization(key)];
    }

    /// <summary>Whether <c>GET /models</c> lists the provider's models. Deepgram and ElevenLabs have no such list for speech models.</summary>
    public static bool ListsModels(this ProviderId id) => id is not (ProviderId.Deepgram or ProviderId.ElevenLabs);

    /// <summary>
    /// What the Test button asks for to check a key, under the base URL: OpenRouter's key endpoint (its model list is
    /// public), Deepgram's projects, ElevenLabs' user; for every other provider the model list.
    /// </summary>
    public static string KeyCheckPath(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "key",
        ProviderId.Deepgram => "projects",
        ProviderId.ElevenLabs => "user",
        _ => "models",
    };

    /// <summary>Volcengine Ark documents no model list: a 404 from it still means the server answered and took the key.</summary>
    public static bool ModelListMayBeMissing(this ProviderId id) => id == ProviderId.Volcengine;

    // MARK: Clean-up requests

    /// <summary>
    /// Whether a clean-up sends <c>temperature</c> (0.2). Not to OpenAI (its reasoning models reject it), Anthropic (Claude
    /// 4.7 and later reject any value under 1), Gemini (Google asks to leave Gemini 3 at its default) or Kimi (its thinking
    /// models fix it); OpenRouter decides by the model's metadata.
    /// </summary>
    public static bool ChatSendsTemperature(this ProviderId id) =>
        id is not (ProviderId.OpenAI or ProviderId.Anthropic or ProviderId.Gemini or ProviderId.Moonshot);

    /// <summary>
    /// Whether a clean-up asks for token counts on the stream (<c>stream_options.include_usage</c>). OpenRouter always sends
    /// them; Mistral sends them anyway and turns down fields it doesn't know.
    /// </summary>
    public static bool ChatSendsStreamOptions(this ProviderId id) => id is not (ProviderId.OpenRouter or ProviderId.Mistral);

    /// <summary>Anthropic's API needs <c>max_tokens</c> on every request, the bare one sent again without the optional fields too.</summary>
    public static bool ChatNeedsMaxTokens(this ProviderId id) => id == ProviderId.Anthropic;

    /// <summary>Whether the clean-up switches the model's reasoning off or to its minimum (see <see cref="ReasoningConfig.Direct"/>).</summary>
    public static bool MinimisesReasoning(this ProviderId id) => id is ProviderId.OpenRouter or ProviderId.Gemini
        or ProviderId.Anthropic or ProviderId.Mistral or ProviderId.XAI or ProviderId.DashScope or ProviderId.Zhipu
        or ProviderId.Moonshot or ProviderId.Volcengine or ProviderId.Ollama;
}

public sealed record ProviderEndpoint(ProviderId Id, Uri BaseUrl, string ApiKey)
{
    /// <summary>Convenience used by tests and the CLI.</summary>
    public static ProviderEndpoint OpenRouter(string apiKey) =>
        new(ProviderId.OpenRouter, new Uri(ProviderId.OpenRouter.DefaultBaseUrl()), apiKey);

    /// <summary>The provider at its default base URL, for tests.</summary>
    public static ProviderEndpoint Preset(ProviderId id, string apiKey) =>
        new(id, new Uri(id.DefaultBaseUrl().Length == 0 ? "http://localhost:8000/v1" : id.DefaultBaseUrl()), apiKey);
}
