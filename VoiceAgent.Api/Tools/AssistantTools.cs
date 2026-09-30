using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Api.Tools;

/// <summary>What the model sees: name, when to use it, and a JSON Schema for its arguments.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

/// <summary>A tool the assistant may call. Register implementations as <see cref="IAssistantTool"/> in DI.</summary>
public interface IAssistantTool
{
    ToolDefinition Definition { get; }
}

/// <summary>
/// Runs inside the API. Its result is fed back to the model, which then continues the reply
/// (e.g. looking up data).
/// </summary>
public interface IServerTool : IAssistantTool
{
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}

/// <summary>
/// Not executed by the API: forwarded to the browser as an <c>action</c> event, because only the
/// browser can perform it (e.g. hanging up the call).
/// </summary>
public interface IClientTool : IAssistantTool
{
}
