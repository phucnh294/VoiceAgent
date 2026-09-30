using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAgent.Api.Services;

/// <summary>
/// A chat message in the provider-neutral shape used by the tool loop. It serializes directly to
/// Ollama's <c>/api/chat</c> format; <c>GeminiChatModel</c> maps it to Gemini's format.
/// </summary>
public sealed record ChatMessage(string Role, string Content)
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";

    /// <summary>Tool calls the assistant made in this message (assistant role only).</summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>Which tool produced this result (tool role only).</summary>
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    /// <summary>The <see cref="ChatToolCall.Id"/> this result answers (tool role only).</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }
}

public sealed record ChatToolCall(ChatToolFunction Function)
{
    /// <summary>Provider-issued call id, echoed back with the result when the provider uses ids.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// Gemini's opaque <c>thoughtSignature</c> for this call. It must be sent back unchanged in the
    /// next request, or thinking models reject the conversation. Not part of Ollama's format.
    /// </summary>
    [JsonIgnore]
    public string? ThoughtSignature { get; init; }
}

public sealed record ChatToolFunction(string Name, JsonElement Arguments);
