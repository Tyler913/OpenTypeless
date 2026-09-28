namespace TypelessCore;

/// <summary>How a provider's <c>/audio/transcriptions</c> endpoint expects audio.</summary>
public enum SttFormat
{
    /// <summary>OpenRouter: JSON body with base64 audio under <c>input_audio</c>.</summary>
    OpenRouterJson,
    /// <summary>OpenAI and every OpenAI-compatible server: multipart/form-data with a <c>file</c> part.</summary>
    Multipart,
}

public enum ProviderId { OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek, Custom }

public static class ProviderIdExtensions
{
    public static readonly ProviderId[] All =
        [ProviderId.OpenRouter, ProviderId.OpenAI, ProviderId.Groq, ProviderId.SiliconFlow, ProviderId.DeepSeek, ProviderId.Custom];

    public static string RawValue(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "openrouter",
        ProviderId.OpenAI => "openai",
        ProviderId.Groq => "groq",
        ProviderId.SiliconFlow => "siliconflow",
        ProviderId.DeepSeek => "deepseek",
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
        _ => L("自定义（OpenAI 兼容）", "Custom (OpenAI-compatible)"),
    };

    public static string DefaultBaseUrl(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "https://openrouter.ai/api/v1",
        ProviderId.OpenAI => "https://api.openai.com/v1",
        ProviderId.Groq => "https://api.groq.com/openai/v1",
        ProviderId.SiliconFlow => "https://api.siliconflow.cn/v1",
        ProviderId.DeepSeek => "https://api.deepseek.com/v1",
        _ => "",
    };

    public static bool SupportsStt(this ProviderId id) => id != ProviderId.DeepSeek;
    public static bool SupportsChat(this ProviderId id) => true;

    public static SttFormat SttFormat(this ProviderId id) => id == ProviderId.OpenRouter ? TypelessCore.SttFormat.OpenRouterJson : TypelessCore.SttFormat.Multipart;

    public static string DefaultSttModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "microsoft/mai-transcribe-2",
        ProviderId.OpenAI => "gpt-4o-transcribe",
        ProviderId.Groq => "whisper-large-v3-turbo",
        ProviderId.SiliconFlow => "FunAudioLLM/SenseVoiceSmall",
        _ => "",
    };

    public static string DefaultChatModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "google/gemini-3.8-flash",
        ProviderId.OpenAI => "gpt-5.6-luna",
        ProviderId.Groq => "openai/gpt-oss-120b",
        ProviderId.SiliconFlow => "Qwen/Qwen2.5-7B-Instruct",
        ProviderId.DeepSeek => "deepseek-chat",
        _ => "",
    };

    /// <summary>
    /// A fast model from a different vendor, so a slow or failing primary rarely drags it down too.
    /// Empty where the user has to pick one.
    /// </summary>
    public static string DefaultBackupChatModel(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "deepseek/deepseek-v4.1-flash",
        _ => "",
    };

    public static string KeyPlaceholder(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => "sk-or-v1-…",
        ProviderId.OpenAI or ProviderId.SiliconFlow or ProviderId.DeepSeek => "sk-…",
        ProviderId.Groq => "gsk_…",
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
        _ => null,
    };

    /// <summary>Local servers (e.g. a self-hosted whisper) often need no key.</summary>
    public static bool RequiresKey(this ProviderId id) => id != ProviderId.Custom;
}

public sealed record ProviderEndpoint(ProviderId Id, Uri BaseUrl, string ApiKey)
{
    /// <summary>Convenience used by tests and the CLI.</summary>
    public static ProviderEndpoint OpenRouter(string apiKey) =>
        new(ProviderId.OpenRouter, new Uri(ProviderId.OpenRouter.DefaultBaseUrl()), apiKey);
}
