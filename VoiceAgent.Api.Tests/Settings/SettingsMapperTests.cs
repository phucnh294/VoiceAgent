using System.Text.Json.Nodes;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Tests.Settings;

public class SettingsMapperTests
{
    private static readonly AgentSettings Stored = new()
    {
        Llm = new LlmSettings
        {
            Provider = LlmProviders.Gemini,
            Gemini = new GeminiSettings { ApiKey = "AIzaSecretKey1234", Model = "gemini-test" },
        },
        Tools = new ToolSettings
        {
            Webhooks =
            [
                new WebhookToolSettings
                {
                    Id = "hook1",
                    Name = "lookup_order",
                    Description = "Looks up an order.",
                    Url = "https://example.test/orders",
                    Headers = new() { ["Authorization"] = "Bearer secret-token-9876" },
                },
            ],
        },
    };

    [Fact]
    public void ToView_Secrets_AreNeverReturned()
    {
        var view = SettingsMapper.ToView(Stored);

        Assert.Null(view.Llm.Gemini.ApiKey);
        Assert.True(view.Llm.Gemini.ApiKeySet);
        Assert.Equal("…1234", view.Llm.Gemini.ApiKeyHint);
        var header = Assert.Single(view.Tools.Webhooks[0].Headers);
        Assert.Null(header.Value);
        Assert.True(header.ValueSet);
        Assert.Equal("…9876", header.ValueHint);
    }

    [Fact]
    public void Apply_NullSecret_KeepsStoredValue()
    {
        var view = SettingsMapper.ToView(Stored); // Secrets come back as null.

        var saved = SettingsMapper.Apply(Stored, view);

        Assert.Equal("AIzaSecretKey1234", saved.Llm.Gemini.ApiKey);
        Assert.Equal("Bearer secret-token-9876", saved.Tools.Webhooks[0].Headers["Authorization"]);
        Assert.Equal("hook1", saved.Tools.Webhooks[0].Id);
    }

    [Fact]
    public void Apply_EmptySecret_ClearsIt()
    {
        var view = SettingsMapper.ToView(Stored);
        view = view with { Llm = view.Llm with { Gemini = view.Llm.Gemini with { ApiKey = "" } } };

        var saved = SettingsMapper.Apply(Stored, view);

        Assert.Null(saved.Llm.Gemini.ApiKey);
    }

    [Fact]
    public void Apply_NewSecret_ReplacesIt()
    {
        var view = SettingsMapper.ToView(Stored);
        view = view with { Llm = view.Llm with { Gemini = view.Llm.Gemini with { ApiKey = "  AIzaNewKey5678  " } } };

        var saved = SettingsMapper.Apply(Stored, view);

        Assert.Equal("AIzaNewKey5678", saved.Llm.Gemini.ApiKey);
    }

    [Fact]
    public void Apply_NewWebhookWithoutId_GetsAnId()
    {
        var view = SettingsMapper.ToView(new AgentSettings());
        view = view with
        {
            Tools = view.Tools with
            {
                Webhooks = [new WebhookView { Name = "new_tool", Description = "d", Url = "https://x.test", Parameters = new JsonObject { ["type"] = "object" } }],
            },
        };

        var saved = SettingsMapper.Apply(new AgentSettings(), view);

        Assert.False(string.IsNullOrEmpty(saved.Tools.Webhooks[0].Id));
    }
}
