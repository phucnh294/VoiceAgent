using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace VoiceAgent.Api.Services;

/// <summary>Typed HttpClient for Ollama's streaming chat endpoint (<c>POST /api/chat</c>).</summary>
public sealed class OllamaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>
    /// Streams the assistant reply token by token. Ollama answers with NDJSON — one JSON object
    /// per line carrying a <c>message.content</c> fragment — until a line with <c>done: true</c>.
    /// </summary>
    /// <exception cref="OllamaException">Ollama is unreachable or reports an error.</exception>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new { model = _options.Model, messages, stream = true };
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
            throw new OllamaException($"Ollama is unreachable at {_http.BaseAddress}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new OllamaException($"Ollama returned {(int)response.StatusCode}: {body}");
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
                    throw new OllamaException($"Ollama error: {error}");
                }

                if (!string.IsNullOrEmpty(chunk?.Message?.Content))
                {
                    yield return chunk.Message.Content;
                }

                if (chunk?.Done == true)
                {
                    yield break;
                }
            }
        }
    }

    private sealed record ChatChunk(ChatMessage? Message, bool Done, string? Error);
}
