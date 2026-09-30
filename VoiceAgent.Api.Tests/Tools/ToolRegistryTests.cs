using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Tests.Tools;

public class ToolRegistryTests
{
    private readonly ToolRegistry _registry = new(new StubHttpClientFactory(), TimeProvider.System);

    [Fact]
    public void Build_Defaults_HasBothBuiltIns()
    {
        var names = _registry.Build(new ToolSettings(), Guid.NewGuid()).Select(tool => tool.Definition.Name);

        Assert.Equal([EndCallTool.Name, CurrentDateTimeTool.Name], names);
    }

    [Fact]
    public void Build_TogglesAndWebhooks_IncludeOnlyEnabled()
    {
        var settings = new ToolSettings
        {
            EndCallEnabled = false,
            DateTimeEnabled = true,
            Webhooks =
            [
                new WebhookToolSettings { Name = "on_tool", Description = "d", Url = "https://x.test", Enabled = true },
                new WebhookToolSettings { Name = "off_tool", Description = "d", Url = "https://x.test", Enabled = false },
            ],
        };

        var tools = _registry.Build(settings, Guid.NewGuid());

        Assert.Equal([CurrentDateTimeTool.Name, "on_tool"], tools.Select(tool => tool.Definition.Name));
        Assert.IsType<WebhookTool>(tools[1]);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
