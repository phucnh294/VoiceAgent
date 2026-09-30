using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Tools;

/// <summary>Builds the tools available in a turn from the current settings.</summary>
public interface IToolRegistry
{
    IReadOnlyList<IAssistantTool> Build(ToolSettings settings, Guid callId);
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;

    public ToolRegistry(IHttpClientFactory httpFactory, TimeProvider time)
    {
        _httpFactory = httpFactory;
        _time = time;
    }

    public IReadOnlyList<IAssistantTool> Build(ToolSettings settings, Guid callId)
    {
        var tools = new List<IAssistantTool>();
        if (settings.EndCallEnabled)
        {
            tools.Add(new EndCallTool());
        }
        if (settings.DateTimeEnabled)
        {
            tools.Add(new CurrentDateTimeTool(_time));
        }
        tools.AddRange(settings.Webhooks
            .Where(webhook => webhook.Enabled)
            .Select(webhook => new WebhookTool(webhook, _httpFactory.CreateClient(WebhookTool.HttpClientName), callId)));
        return tools;
    }
}
