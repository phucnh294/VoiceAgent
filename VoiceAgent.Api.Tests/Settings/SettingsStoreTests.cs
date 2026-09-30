using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"settings-tests-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_root, "settings.json");

    [Fact]
    public void Constructor_NoFile_SeedsFromAppSettingsAndWritesFile()
    {
        var store = CreateStore();

        Assert.Equal("http://seed:11434", store.Current.Llm.Ollama.BaseUrl);
        Assert.Equal("Seed prompt", store.Current.Assistant.SystemPrompt);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public async Task SaveAsync_ThenReload_ReturnsSavedSettings()
    {
        var store = CreateStore();
        var updated = store.Current with
        {
            Voice = store.Current.Voice with { Kokoro = store.Current.Voice.Kokoro with { Voice = "bf_emma", Speed = 1.3 } },
        };

        await store.SaveAsync(updated);
        var reloaded = CreateStore();

        Assert.Equal("bf_emma", reloaded.Current.Voice.Kokoro.Voice);
        Assert.Equal(1.3, reloaded.Current.Voice.Kokoro.Speed);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Constructor_CorruptFile_UsesDefaultsAndKeepsFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{ not json");

        var store = CreateStore();

        Assert.Equal(new OllamaSettings().BaseUrl, store.Current.Llm.Ollama.BaseUrl);
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SettingsStore CreateStore() => new(
        Options.Create(new SettingsFileOptions { FilePath = FilePath }),
        Options.Create(new OllamaOptions { BaseUrl = "http://seed:11434", Model = "seed-model" }),
        Options.Create(new AssistantOptions { SystemPrompt = "Seed prompt" }),
        new StubHostEnvironment(),
        NullLogger<SettingsStore>.Instance);

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
