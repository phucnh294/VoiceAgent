using System.Text.Json.Nodes;

namespace VoiceAgent.Api.Tools;

/// <summary>Hangs up the call in the browser once the farewell has been spoken.</summary>
public sealed class EndCallTool : IClientTool
{
    public const string Name = "end_call";

    public ToolDefinition Definition { get; } = new(
        Name,
        "End the phone call. Call this only when the caller clearly wants to finish, for example "
        + "they say goodbye, bye, that's all, quit, or ask to hang up.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["farewell"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A short goodbye sentence to say before hanging up.",
                },
            },
        });
}
