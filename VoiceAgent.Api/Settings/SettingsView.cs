using System.Text.Json.Nodes;

namespace VoiceAgent.Api.Settings;

/// <summary>
/// The settings as exchanged with the browser. Identical to <see cref="AgentSettings"/> except that
/// secrets (the Gemini API key, webhook header values) are write-only:
/// <list type="bullet">
/// <item>On GET they are always null, with <c>…Set</c>/<c>…Hint</c> telling the UI a value exists.</item>
/// <item>On PUT, null keeps the stored secret, "" clears it, anything else replaces it.</item>
/// </list>
/// </summary>
public sealed record SettingsView
{
    public AssistantSettings Assistant { get; init; } = new();
    public LlmView Llm { get; init; } = new();
    public VoiceSettings Voice { get; init; } = new();
    public ToolsView Tools { get; init; } = new();
}

public sealed record LlmView
{
    public string Provider { get; init; } = LlmProviders.Ollama;
    public OllamaSettings Ollama { get; init; } = new();
    public GeminiView Gemini { get; init; } = new();
}

public sealed record GeminiView
{
    public string? ApiKey { get; init; }
    public bool ApiKeySet { get; init; }
    public string? ApiKeyHint { get; init; }
    public string Model { get; init; } = string.Empty;
}

public sealed record ToolsView
{
    public bool EndCallEnabled { get; init; } = true;
    public bool DateTimeEnabled { get; init; } = true;
    public List<WebhookView> Webhooks { get; init; } = [];
}

public sealed record WebhookView
{
    /// <summary>Null for a webhook added in the UI and not saved yet.</summary>
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonObject? Parameters { get; init; }
    public string Url { get; init; } = string.Empty;
    public List<HeaderView> Headers { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 10;
    public bool Enabled { get; init; } = true;
}

public sealed record HeaderView
{
    public string Name { get; init; } = string.Empty;
    public string? Value { get; init; }
    public bool ValueSet { get; init; }
    public string? ValueHint { get; init; }
}

/// <summary>Converts between stored settings and the browser view, applying the secret rules.</summary>
public static class SettingsMapper
{
    public static SettingsView ToView(AgentSettings settings) => new()
    {
        Assistant = settings.Assistant,
        Voice = settings.Voice,
        Llm = new LlmView
        {
            Provider = settings.Llm.Provider,
            Ollama = settings.Llm.Ollama,
            Gemini = new GeminiView
            {
                ApiKeySet = !string.IsNullOrEmpty(settings.Llm.Gemini.ApiKey),
                ApiKeyHint = Hint(settings.Llm.Gemini.ApiKey),
                Model = settings.Llm.Gemini.Model,
            },
        },
        Tools = new ToolsView
        {
            EndCallEnabled = settings.Tools.EndCallEnabled,
            DateTimeEnabled = settings.Tools.DateTimeEnabled,
            Webhooks = settings.Tools.Webhooks.Select(webhook => new WebhookView
            {
                Id = webhook.Id,
                Name = webhook.Name,
                Description = webhook.Description,
                Parameters = webhook.Parameters,
                Url = webhook.Url,
                Headers = webhook.Headers
                    .Select(header => new HeaderView { Name = header.Key, ValueSet = true, ValueHint = Hint(header.Value) })
                    .ToList(),
                TimeoutSeconds = webhook.TimeoutSeconds,
                Enabled = webhook.Enabled,
            }).ToList(),
        },
    };

    /// <summary>Builds the settings to save from an edited view, keeping secrets the view left as null.</summary>
    public static AgentSettings Apply(AgentSettings current, SettingsView update)
    {
        var storedWebhooks = current.Tools.Webhooks.ToDictionary(webhook => webhook.Id);
        return new AgentSettings
        {
            Assistant = update.Assistant,
            Voice = update.Voice,
            Llm = new LlmSettings
            {
                Provider = update.Llm.Provider,
                Ollama = update.Llm.Ollama,
                Gemini = new GeminiSettings
                {
                    ApiKey = MergeSecret(current.Llm.Gemini.ApiKey, update.Llm.Gemini.ApiKey),
                    Model = update.Llm.Gemini.Model,
                },
            },
            Tools = new ToolSettings
            {
                EndCallEnabled = update.Tools.EndCallEnabled,
                DateTimeEnabled = update.Tools.DateTimeEnabled,
                Webhooks = update.Tools.Webhooks.Select(view =>
                {
                    var stored = view.Id is not null ? storedWebhooks.GetValueOrDefault(view.Id) : null;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in view.Headers)
                    {
                        var value = MergeSecret(stored?.Headers.GetValueOrDefault(header.Name), header.Value);
                        if (!string.IsNullOrEmpty(value))
                        {
                            headers[header.Name.Trim()] = value;
                        }
                    }
                    return new WebhookToolSettings
                    {
                        Id = stored?.Id ?? Guid.NewGuid().ToString("N"),
                        Name = view.Name.Trim(),
                        Description = view.Description.Trim(),
                        Parameters = view.Parameters,
                        Url = view.Url.Trim(),
                        Headers = headers,
                        TimeoutSeconds = view.TimeoutSeconds,
                        Enabled = view.Enabled,
                    };
                }).ToList(),
            },
        };
    }

    private static string? MergeSecret(string? stored, string? update) => update switch
    {
        null => stored,
        "" => null,
        _ => update.Trim(),
    };

    private static string? Hint(string? secret) => string.IsNullOrEmpty(secret)
        ? null
        : secret.Length >= 8 ? $"…{secret[^4..]}" : "…";
}
