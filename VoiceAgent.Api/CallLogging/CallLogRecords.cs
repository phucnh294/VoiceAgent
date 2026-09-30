using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.CallLogging;

/// <summary>
/// One line of a call log file (JSON Lines), discriminated by <c>type</c>. <see cref="Timestamp"/>
/// and <see cref="CallId"/> are stamped by the writer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ModelRequestRecord), "model_request")]
[JsonDerivedType(typeof(ModelResponseRecord), "model_response")]
[JsonDerivedType(typeof(ToolCallRecord), "tool_call")]
[JsonDerivedType(typeof(TurnRecord), "turn")]
[JsonDerivedType(typeof(CallEndRecord), "call_end")]
public abstract record CallLogRecord
{
    public DateTimeOffset Timestamp { get; init; }

    public Guid CallId { get; init; }
}

/// <summary>Serialized in lowercase (<c>"completed"</c>) by <see cref="JsonlCallLog"/>.</summary>
public enum TurnStatus
{
    /// <summary>The reply was fully generated and sent.</summary>
    Completed,

    /// <summary>The browser aborted the request — the caller interrupted or hung up.</summary>
    Cancelled,

    /// <summary>The model or a tool failed; see the API log for the exception.</summary>
    Failed,
}

/// <summary>Exactly what was sent to the model, system prompt included.</summary>
public sealed record ModelRequestRecord(
    int Turn,
    int Round,
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> Tools) : CallLogRecord;

/// <summary>What the model produced for one request (partial if the turn was cut short).</summary>
public sealed record ModelResponseRecord(
    int Turn,
    int Round,
    string Text,
    IReadOnlyList<ChatToolCall> ToolCalls,
    TurnStatus Status,
    double DurationMs) : CallLogRecord;

/// <summary>A tool the model called: executed on the server, or forwarded to the browser.</summary>
public sealed record ToolCallRecord(
    int Turn,
    int Round,
    string Tool,
    string RunsOn,
    JsonElement Arguments,
    string? Result,
    double DurationMs) : CallLogRecord;

/// <summary>Summary of one caller turn: input, the reply sent to the browser, outcome.</summary>
public sealed record TurnRecord(
    int Turn,
    TurnStatus Status,
    string CallerSaid,
    string AssistantReplied,
    IReadOnlyList<ActionLogEntry> Actions,
    int ModelRounds,
    double DurationMs) : CallLogRecord;

/// <param name="Source"><c>model</c> (tool call) or <c>farewell_detector</c> (backstop).</param>
public sealed record ActionLogEntry(string Name, JsonElement Arguments, string Source);

/// <summary>Reported by the browser when the call ends, with the transcript as the caller saw it.</summary>
public sealed record CallEndRecord(
    string Reason,
    int DurationSeconds,
    IReadOnlyList<TranscriptEntry> Transcript) : CallLogRecord;

public sealed record TranscriptEntry(string Role, string Content, bool Interrupted = false, bool Typed = false);
