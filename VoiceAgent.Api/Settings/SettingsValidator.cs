using System.Text.RegularExpressions;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Settings;

/// <summary>Checks settings before they are saved; returns field path → messages.</summary>
public static partial class SettingsValidator
{
    private static readonly HashSet<string> BuiltInTools = [EndCallTool.Name, CurrentDateTimeTool.Name];

    public static Dictionary<string, string[]> Validate(AgentSettings settings)
    {
        var errors = new Dictionary<string, List<string>>();
        void Fail(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }
            list.Add(message);
        }

        var assistant = settings.Assistant;
        if (assistant.SystemPrompt.Length > 20_000) Fail("assistant.systemPrompt", "Keep the system prompt under 20,000 characters.");
        if (string.IsNullOrWhiteSpace(assistant.Greeting)) Fail("assistant.greeting", "The greeting is required.");
        if (assistant.Greeting.Length > 500) Fail("assistant.greeting", "Keep the greeting under 500 characters.");
        if (assistant.MaxHistoryMessages is < 2 or > 100) Fail("assistant.maxHistoryMessages", "Use 2 to 100.");
        if (assistant.MaxToolRounds is < 1 or > 10) Fail("assistant.maxToolRounds", "Use 1 to 10.");

        var llm = settings.Llm;
        switch (llm.Provider)
        {
            case LlmProviders.Ollama:
                if (!IsHttpUrl(llm.Ollama.BaseUrl)) Fail("llm.ollama.baseUrl", "Enter an http(s) URL, e.g. http://localhost:11434.");
                if (string.IsNullOrWhiteSpace(llm.Ollama.Model)) Fail("llm.ollama.model", "Pick a model.");
                break;
            case LlmProviders.Gemini:
                if (string.IsNullOrEmpty(llm.Gemini.ApiKey)) Fail("llm.gemini.apiKey", "Enter your Gemini API key.");
                if (string.IsNullOrWhiteSpace(llm.Gemini.Model)) Fail("llm.gemini.model", "Pick a model.");
                break;
            default:
                Fail("llm.provider", "Choose ollama or gemini.");
                break;
        }

        var voice = settings.Voice;
        if (voice.Provider is not (VoiceProviders.Kokoro or VoiceProviders.Browser)) Fail("voice.provider", "Choose kokoro or browser.");
        if (!IsHttpUrl(voice.Kokoro.BaseUrl)) Fail("voice.kokoro.baseUrl", "Enter an http(s) URL, e.g. http://localhost:8880.");
        if (voice.Provider == VoiceProviders.Kokoro && string.IsNullOrWhiteSpace(voice.Kokoro.Voice)) Fail("voice.kokoro.voice", "Pick a voice.");
        if (voice.Kokoro.Speed is < 0.5 or > 2.0) Fail("voice.kokoro.speed", "Use 0.5 to 2.0.");
        if (voice.Browser.Rate is < 0.5 or > 2.0) Fail("voice.browser.rate", "Use 0.5 to 2.0.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < settings.Tools.Webhooks.Count; i++)
        {
            var webhook = settings.Tools.Webhooks[i];
            var prefix = $"tools.webhooks[{i}]";
            if (!ToolName().IsMatch(webhook.Name))
            {
                Fail($"{prefix}.name", "Use letters, digits and underscores, starting with a letter or underscore (max 64).");
            }
            else if (BuiltInTools.Contains(webhook.Name))
            {
                Fail($"{prefix}.name", $"'{webhook.Name}' is a built-in tool name.");
            }
            else if (!names.Add(webhook.Name))
            {
                Fail($"{prefix}.name", $"Another tool is already named '{webhook.Name}'.");
            }
            if (string.IsNullOrWhiteSpace(webhook.Description)) Fail($"{prefix}.description", "Describe when the model should call this tool.");
            if (!IsHttpUrl(webhook.Url)) Fail($"{prefix}.url", "Enter an http(s) URL.");
            if (webhook.TimeoutSeconds is < 1 or > 60) Fail($"{prefix}.timeoutSeconds", "Use 1 to 60 seconds.");
            if (webhook.Parameters is { } schema
                && (schema["type"]?.GetValueKind() != System.Text.Json.JsonValueKind.String || (string?)schema["type"] != "object"))
            {
                Fail($"{prefix}.parameters", "The parameters schema must be a JSON object with \"type\": \"object\".");
            }
            foreach (var header in webhook.Headers.Keys.Where(name => !HeaderName().IsMatch(name)))
            {
                Fail($"{prefix}.headers", $"'{header}' is not a valid header name.");
            }
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]{0,63}$")]
    private static partial Regex ToolName();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();
}
