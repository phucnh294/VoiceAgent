using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Api.Tools;

/// <summary>Gives the model the real date and time, which it otherwise invents.</summary>
public sealed class CurrentDateTimeTool : IServerTool
{
    public const string Name = "get_current_datetime";

    private readonly TimeProvider _time;

    public CurrentDateTimeTool(TimeProvider time)
    {
        _time = time;
    }

    public ToolDefinition Definition { get; } = new(
        Name,
        "Get the current local date and time. Use it whenever the caller asks what time, day or "
        + "date it is; never guess.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var now = _time.GetLocalNow();
        var text = now.ToString("dddd, MMMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
        return Task.FromResult($"{text} ({_time.LocalTimeZone.DisplayName})");
    }
}
