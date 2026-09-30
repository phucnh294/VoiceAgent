using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAgent.Api.Services;

/// <summary>A message in the chat model's wire format (Ollama <c>/api/chat</c>).</summary>
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
}

public sealed record ChatToolCall(ChatToolFunction Function);

public sealed record ChatToolFunction(string Name, JsonElement Arguments);
