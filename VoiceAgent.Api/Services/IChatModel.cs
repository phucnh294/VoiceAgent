using System.Text.Json;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Services;

/// <summary>A streaming chat LLM that can request tool calls.</summary>
public interface IChatModel
{
    /// <summary>Which model answers, for logs.</summary>
    string ModelName { get; }

    /// <summary>Streams one model response as text fragments and/or tool call requests.</summary>
    /// <exception cref="ChatModelException">The model is unreachable or reports an error.</exception>
    IAsyncEnumerable<ChatStreamItem> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}

public abstract record ChatStreamItem;

public sealed record TextDelta(string Text) : ChatStreamItem;

public sealed record ToolCallRequest(string Name, JsonElement Arguments) : ChatStreamItem;
