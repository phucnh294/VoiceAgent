using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAgent.Api.Conversation;

/// <summary>
/// One line of the <c>/api/conversation/stream</c> NDJSON response, discriminated by <c>type</c>:
/// <c>{"type":"text","text":"…"}</c>, <c>{"type":"action","name":"end_call","arguments":{…}}</c>
/// or <c>{"type":"error","message":"…"}</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextEvent), "text")]
[JsonDerivedType(typeof(ActionEvent), "action")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record ConversationEvent;

/// <summary>A fragment of the spoken reply.</summary>
public sealed record TextEvent(string Text) : ConversationEvent;

/// <summary>A client tool call the browser must perform.</summary>
public sealed record ActionEvent(string Name, JsonElement Arguments) : ConversationEvent;

/// <summary>The reply failed after streaming had already started.</summary>
public sealed record ErrorEvent(string Message) : ConversationEvent;
