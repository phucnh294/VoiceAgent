using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Services;

/// <summary>
/// Google Gemini via the native REST API:
/// <c>POST /v1beta/models/{model}:streamGenerateContent?alt=sse</c> with <c>x-goog-api-key</c>.
/// </summary>
public sealed class GeminiChatModel : IChatModel
{
    public const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/";

    /// <summary>Gemini expects the conversation to open with a user turn; our calls open with the greeting.</summary>
    internal const string CallStartedPlaceholder = "(The call has connected.)";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly GeminiSettings _settings;

    public GeminiChatModel(HttpClient http, GeminiSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public string ModelName => $"{LlmProviders.Gemini}:{_settings.Model}";

    public async IAsyncEnumerable<ChatStreamItem> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}models/{Uri.EscapeDataString(_settings.Model)}:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(BuildRequest(messages, tools)),
        };
        request.Headers.Add("x-goog-api-key", _settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelException("Gemini is unreachable. Check the internet connection.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new ChatModelException($"Gemini returned {(int)response.StatusCode}: {ErrorMessage(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                // Server-sent events: payload lines start with "data:"; blank lines separate events.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (var item in ParseChunk(line["data:".Length..].Trim()))
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>Lists models that support chat (<c>generateContent</c>) for this API key.</summary>
    /// <exception cref="ChatModelException">The key is invalid or Gemini is unreachable.</exception>
    public static async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(HttpClient http, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}models?pageSize=1000");
        request.Headers.Add("x-goog-api-key", apiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatModelException($"Gemini returned {(int)response.StatusCode}: {ErrorMessage(body)}");
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models))
            {
                return [];
            }
            return models.EnumerateArray()
                .Where(model => model.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.EnumerateArray().Any(method => method.GetString() == "generateContent"))
                .Select(model =>
                {
                    var id = model.GetProperty("name").GetString()!.Replace("models/", "", StringComparison.Ordinal);
                    var display = model.TryGetProperty("displayName", out var name) ? name.GetString() ?? id : id;
                    return new ModelInfo(id, $"{display} ({id})");
                })
                .OrderBy(model => model.Id, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new ChatModelException($"Could not list Gemini models: {ex.Message}", ex);
        }
    }

    /// <summary>Maps the provider-neutral conversation to a Gemini request body.</summary>
    internal static JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
    {
        // Gemini has a single system instruction: merge all system messages, in order.
        var systemText = string.Join("\n\n", messages.Where(m => m.Role == ChatMessage.System).Select(m => m.Content));

        var contents = new List<(string Role, JsonArray Parts)>();
        foreach (var message in messages.Where(m => m.Role != ChatMessage.System))
        {
            var (role, parts) = message.Role switch
            {
                ChatMessage.Assistant => ("model", AssistantParts(message)),
                ChatMessage.Tool => ("user", new JsonArray(FunctionResponsePart(message))),
                _ => ("user", new JsonArray(new JsonObject { ["text"] = message.Content })),
            };
            if (parts.Count == 0)
            {
                continue;
            }
            // Gemini wants alternating turns; consecutive same-role messages (e.g. several tool
            // results) become one turn with several parts.
            if (contents.Count > 0 && contents[^1].Role == role)
            {
                foreach (var part in parts.ToList())
                {
                    parts.Remove(part);
                    contents[^1].Parts.Add(part);
                }
            }
            else
            {
                contents.Add((role, parts));
            }
        }
        if (contents.Count > 0 && contents[0].Role == "model")
        {
            contents.Insert(0, ("user", new JsonArray(new JsonObject { ["text"] = CallStartedPlaceholder })));
        }

        var request = new JsonObject
        {
            ["contents"] = new JsonArray(contents
                .Select(content => (JsonNode)new JsonObject { ["role"] = content.Role, ["parts"] = content.Parts })
                .ToArray()),
        };
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            request["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemText }) };
        }
        if (tools.Count > 0)
        {
            request["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(tools.Select(FunctionDeclaration).ToArray()),
            });
        }
        return request;
    }

    internal static IEnumerable<ChatStreamItem> ParseChunk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new ChatModelException($"Gemini error: {error.GetProperty("message").GetString()}");
        }
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var reason))
        {
            throw new ChatModelException($"Gemini blocked the request ({reason.GetString()}).");
        }
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0
            || !candidates[0].TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts))
        {
            return [];
        }

        var items = new List<ChatStreamItem>();
        foreach (var part in parts.EnumerateArray())
        {
            var isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
            if (part.TryGetProperty("functionCall", out var call))
            {
                items.Add(new ToolCallRequest(
                    call.GetProperty("name").GetString()!,
                    call.TryGetProperty("args", out var args) ? args.Clone() : JsonDocument.Parse("{}").RootElement.Clone(),
                    call.TryGetProperty("id", out var id) ? id.GetString() : null,
                    part.TryGetProperty("thoughtSignature", out var signature) ? signature.GetString() : null));
            }
            else if (!isThought && part.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } value)
            {
                items.Add(new TextDelta(value));
            }
        }
        return items;
    }

    private static JsonArray AssistantParts(ChatMessage message)
    {
        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(message.Content))
        {
            parts.Add(new JsonObject { ["text"] = message.Content });
        }
        foreach (var call in message.ToolCalls ?? [])
        {
            var functionCall = new JsonObject
            {
                ["name"] = call.Function.Name,
                ["args"] = JsonNode.Parse(call.Function.Arguments.GetRawText()),
            };
            if (call.Id is not null)
            {
                functionCall["id"] = call.Id;
            }
            var part = new JsonObject { ["functionCall"] = functionCall };
            if (call.ThoughtSignature is not null)
            {
                part["thoughtSignature"] = call.ThoughtSignature; // Must round-trip unchanged.
            }
            parts.Add(part);
        }
        return parts;
    }

    private static JsonObject FunctionResponsePart(ChatMessage message)
    {
        var response = new JsonObject
        {
            ["name"] = message.ToolName,
            ["response"] = new JsonObject { ["result"] = message.Content },
        };
        if (message.ToolCallId is not null)
        {
            response["id"] = message.ToolCallId;
        }
        return new JsonObject { ["functionResponse"] = response };
    }

    private static JsonNode FunctionDeclaration(ToolDefinition tool)
    {
        var declaration = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
        // A tool without arguments must omit the schema; an empty "properties" object is rejected.
        if (tool.Parameters["properties"] is JsonObject { Count: > 0 })
        {
            declaration["parametersJsonSchema"] = tool.Parameters.DeepClone();
        }
        return declaration;
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }
}
