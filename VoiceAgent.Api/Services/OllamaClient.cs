using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Services;

/// <summary>Typed HttpClient for Ollama's streaming chat endpoint (<c>POST /api/chat</c>).</summary>
public sealed class OllamaClient : IChatModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string ModelName => _options.Model;

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
            model = _options.Model,
            messages,
            tools = tools.Count > 0 ? tools.Select(OllamaTool.From).ToList() : null,
            stream = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
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
            throw new ChatModelException($"Ollama is unreachable at {_http.BaseAddress}.", ex);
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
                    yield return new ToolCallRequest(call.Function.Name, call.Function.Arguments);
                }

                if (chunk?.Done == true)
                {
                    yield break;
                }
            }
        }
    }

    private sealed record ChatChunk(ChatMessage? Message, bool Done, string? Error);

    private sealed record OllamaTool(string Type, OllamaToolFunction Function)
    {
        public static OllamaTool From(ToolDefinition tool) =>
            new("function", new OllamaToolFunction(tool.Name, tool.Description, tool.Parameters));
    }

    private sealed record OllamaToolFunction(string Name, string Description, object Parameters);
}
