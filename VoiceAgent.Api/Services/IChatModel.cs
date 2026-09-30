using System.Text.Json;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Services;

/// <summary>A streaming chat LLM that can request tool calls.</summary>
public interface IChatModel
{
    /// <summary>Which provider and model answers, e.g. <c>gemini:gemini-2.5-flash</c>, for logs.</summary>
    string ModelName { get; }

    /// <summary>Streams one model response as text fragments and/or tool call requests.</summary>
    /// <exception cref="ChatModelException">The model is unreachable or reports an error.</exception>
    IAsyncEnumerable<ChatStreamItem> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}

/// <summary>Picks the chat model for a turn from the current LLM settings.</summary>
public interface IChatModelResolver
{
    IChatModel Resolve(LlmSettings settings);
}

public abstract record ChatStreamItem;

public sealed record TextDelta(string Text) : ChatStreamItem;

/// <param name="Id">Provider-issued call id, if any.</param>
/// <param name="ThoughtSignature">Gemini's signature to round-trip with this call, if any.</param>
public sealed record ToolCallRequest(string Name, JsonElement Arguments, string? Id = null, string? ThoughtSignature = null)
    : ChatStreamItem;
