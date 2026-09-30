using System.Text.Json.Nodes;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Tests.Settings;

public class SettingsValidatorTests
{
    [Fact]
    public void Validate_Defaults_AreValid()
    {
        Assert.Empty(SettingsValidator.Validate(new AgentSettings()));
    }

    [Fact]
    public void Validate_GeminiWithoutKeyOrModel_ReportsBoth()
    {
        var settings = new AgentSettings { Llm = new LlmSettings { Provider = LlmProviders.Gemini } };

        var errors = SettingsValidator.Validate(settings);

        Assert.Contains("llm.gemini.apiKey", errors.Keys);
        Assert.Contains("llm.gemini.model", errors.Keys);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(2.5)]
    public void Validate_SpeedOutOfRange_IsRejected(double speed)
    {
        var settings = new AgentSettings { Voice = new VoiceSettings { Kokoro = new KokoroSettings { Speed = speed } } };

        Assert.Contains("voice.kokoro.speed", SettingsValidator.Validate(settings).Keys);
    }

    [Theory]
    [InlineData("1bad", "tools.webhooks[0].name")]
    [InlineData("end_call", "tools.webhooks[0].name")]
    [InlineData("has space", "tools.webhooks[0].name")]
    public void Validate_BadWebhookName_IsRejected(string name, string field)
    {
        var settings = WithWebhooks(Webhook(name));

        Assert.Contains(field, SettingsValidator.Validate(settings).Keys);
    }

    [Fact]
    public void Validate_DuplicateWebhookNames_AreRejected()
    {
        var settings = WithWebhooks(Webhook("lookup"), Webhook("Lookup"));

        Assert.Contains("tools.webhooks[1].name", SettingsValidator.Validate(settings).Keys);
    }

    [Fact]
    public void Validate_SchemaNotAnObject_IsRejected()
    {
        var settings = WithWebhooks(Webhook("lookup") with { Parameters = new JsonObject { ["type"] = "string" } });

        Assert.Contains("tools.webhooks[0].parameters", SettingsValidator.Validate(settings).Keys);
    }

    [Fact]
    public void Validate_NonHttpUrl_IsRejected()
    {
        var settings = WithWebhooks(Webhook("lookup") with { Url = "file:///etc/passwd" });

        Assert.Contains("tools.webhooks[0].url", SettingsValidator.Validate(settings).Keys);
    }

    private static WebhookToolSettings Webhook(string name) => new()
    {
        Name = name,
        Description = "Looks something up.",
        Url = "https://example.test/hook",
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    private static AgentSettings WithWebhooks(params WebhookToolSettings[] webhooks) =>
        new() { Tools = new ToolSettings { Webhooks = [.. webhooks] } };
}
