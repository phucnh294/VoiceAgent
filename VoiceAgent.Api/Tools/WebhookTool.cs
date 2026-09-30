using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Tools;

/// <summary>
/// A tool defined in the Settings UI. When the model calls it, the API POSTs
/// <c>{ "tool": name, "arguments": {...}, "callId": "..." }</c> to the configured URL and hands the
/// response body back to the model.
/// </summary>
public sealed class WebhookTool : IServerTool
{
    public const string HttpClientName = "webhooks";

    /// <summary>Long responses would flood the model's context and slow the spoken reply.</summary>
    public const int MaxResultChars = 4000;

    private static readonly JsonObject NoArguments = new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private readonly WebhookToolSettings _settings;
    private readonly HttpClient _http;
    private readonly Guid _callId;

    public WebhookTool(WebhookToolSettings settings, HttpClient http, Guid callId)
    {
        _settings = settings;
        _http = http;
        _callId = callId;
        Definition = new ToolDefinition(
            settings.Name,
            settings.Description,
            (JsonObject)(settings.Parameters ?? NoArguments).DeepClone());
    }

    public ToolDefinition Definition { get; }

    /// <summary>Never throws for webhook failures: the model gets an <c>Error: …</c> result to talk about instead.</summary>
    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Url)
        {
            Content = JsonContent.Create(new { tool = _settings.Name, arguments, callId = _callId }),
        };
        foreach (var (name, value) in _settings.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await _http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return response.IsSuccessStatusCode
                ? Truncate(body, MaxResultChars)
                : $"Error: the tool returned HTTP {(int)response.StatusCode}. {Truncate(body, 500)}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Error: the tool did not answer within {_settings.TimeoutSeconds} seconds.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: the tool could not be reached ({ex.Message}).";
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " …(truncated)";
}
