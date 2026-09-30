using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Services;

/// <summary>Ollama's streaming chat endpoint (<c>POST {BaseUrl}/api/chat</c>).</summary>
public sealed class OllamaClient : IChatModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OllamaSettings _settings;

    public OllamaClient(HttpClient http, OllamaSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public string ModelName => $"{LlmProviders.Ollama}:{_settings.Model}";

    /// <summary>
    /// Streams one response. Ollama answers with NDJSON — one JSON object per line carrying a
    /// <c>message.content</c> fragment and/or <c>message.tool_calls</c> — until <c>done: true</c>.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamItem> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new
        {
            model = _settings.Model,
            messages,
            tools = tools.Count > 0 ? tools.Select(OllamaTool.From).ToList() : null,
            stream = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(_settings.BaseUrl, "api/chat"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelException($"Ollama is unreachable at {_settings.BaseUrl}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new ChatModelException($"Ollama returned {(int)response.StatusCode}: {body}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var chunk = JsonSerializer.Deserialize<ChatChunk>(line, JsonOptions);
                if (chunk?.Error is { } error)
                {
                    throw new ChatModelException($"Ollama error: {error}");
                }

                if (!string.IsNullOrEmpty(chunk?.Message?.Content))
                {
                    yield return new TextDelta(chunk.Message.Content);
                }

                foreach (var call in chunk?.Message?.ToolCalls ?? [])
                {
                    yield return new ToolCallRequest(call.Function.Name, call.Function.Arguments, call.Id);
                }

                if (chunk?.Done == true)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Lists installed models (<c>GET {baseUrl}/api/tags</c>).</summary>
    /// <exception cref="ChatModelException">Ollama is unreachable or returns an error.</exception>
    public static async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            var tags = await http.GetFromJsonAsync<TagsResponse>(Endpoint(baseUrl, "api/tags"), JsonOptions, ct);
            return (tags?.Models ?? []).Select(model => new ModelInfo(model.Name, model.Name)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException)
        {
            throw new ChatModelException($"Could not list Ollama models at {baseUrl}: {ex.Message}", ex);
        }
    }

    private static Uri Endpoint(string baseUrl, string path) => new(new Uri(baseUrl.TrimEnd('/') + "/"), path);

    private sealed record ChatChunk(ChatMessage? Message, bool Done, string? Error);

    private sealed record TagsResponse(List<TagModel>? Models);

    private sealed record TagModel(string Name);

    private sealed record OllamaTool(string Type, OllamaToolFunction Function)
    {
        public static OllamaTool From(ToolDefinition tool) =>
            new("function", new OllamaToolFunction(tool.Name, tool.Description, tool.Parameters));
    }

    private sealed record OllamaToolFunction(string Name, string Description, object Parameters);
}

/// <param name="Id">What to store in settings.</param>
/// <param name="DisplayName">What to show in the model dropdown.</param>
public sealed record ModelInfo(string Id, string DisplayName);
