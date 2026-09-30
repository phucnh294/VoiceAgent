using System.Text.Json.Nodes;

namespace VoiceAgent.Api.Settings;

/// <summary>
/// Everything an operator can change at runtime from the Settings panel. Instances are treated
/// as immutable snapshots: <see cref="SettingsStore"/> swaps the whole object on save, so a call
/// turn that read <c>Current</c> never sees half-applied settings.
/// </summary>
public sealed record AgentSettings
{
    public AssistantSettings Assistant { get; init; } = new();
    public LlmSettings Llm { get; init; } = new();
    public VoiceSettings Voice { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
}

public sealed record AssistantSettings
{
    public string SystemPrompt { get; init; } = string.Empty;
    public string Greeting { get; init; } = "Hello, thanks for calling. How can I help you today?";
    public string ToolInstructions { get; init; } = string.Empty;
    public string FarewellInstruction { get; init; } = string.Empty;
    /// <summary>Empty uses the built-in list in <c>FarewellDetector</c>.</summary>
    public List<string> EndCallPhrases { get; init; } = [];
    public int MaxHistoryMessages { get; init; } = 20;
    public int MaxToolRounds { get; init; } = 3;
}

public static class LlmProviders
{
    public const string Ollama = "ollama";
    public const string Gemini = "gemini";
}

public sealed record LlmSettings
{
    public string Provider { get; init; } = LlmProviders.Ollama;
    public OllamaSettings Ollama { get; init; } = new();
    public GeminiSettings Gemini { get; init; } = new();
}

public sealed record OllamaSettings
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen2.5:0.5b-instruct";
}

public sealed record GeminiSettings
{
    /// <summary>Secret: never returned to the browser.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Model id without the <c>models/</c> prefix, picked from the live model list.</summary>
    public string Model { get; init; } = string.Empty;
}

public static class VoiceProviders
{
    public const string Kokoro = "kokoro";
    public const string Browser = "browser";
}

public sealed record VoiceSettings
{
    public string Provider { get; init; } = VoiceProviders.Kokoro;
    public KokoroSettings Kokoro { get; init; } = new();
    public BrowserVoiceSettings Browser { get; init; } = new();
}

public sealed record KokoroSettings
{
    public string BaseUrl { get; init; } = "http://localhost:8880";
    public string Voice { get; init; } = "af_heart";
    public double Speed { get; init; } = 1.0;
}

public sealed record BrowserVoiceSettings
{
    /// <summary>A <c>SpeechSynthesisVoice.name</c>; null uses the browser default for the call language.</summary>
    public string? VoiceName { get; init; }
    public double Rate { get; init; } = 1.0;
}

public sealed record ToolSettings
{
    public bool EndCallEnabled { get; init; } = true;
    public bool DateTimeEnabled { get; init; } = true;
    public List<WebhookToolSettings> Webhooks { get; init; } = [];
}

/// <summary>A tool defined in the UI: the API POSTs the model's arguments to <see cref="Url"/>.</summary>
public sealed record WebhookToolSettings
{
    /// <summary>Stable identity across renames, so masked header secrets can be kept on save.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>JSON Schema (<c>type: object</c>) for the arguments; null means no arguments.</summary>
    public JsonObject? Parameters { get; init; }
    public string Url { get; init; } = string.Empty;
    /// <summary>Sent with every call, e.g. <c>Authorization</c>. Values are secrets.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 10;
    public bool Enabled { get; init; } = true;
}
