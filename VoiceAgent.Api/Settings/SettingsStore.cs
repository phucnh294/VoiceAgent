using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.Settings;

/// <summary>Read access to the live settings; implemented by <see cref="SettingsStore"/>, faked in tests.</summary>
public interface ISettingsProvider
{
    AgentSettings Current { get; }
}

public sealed class SettingsFileOptions
{
    public const string SectionName = "Settings";

    /// <summary>Where runtime settings (including API keys) are saved; relative to the API content root.</summary>
    public string FilePath { get; set; } = "data/settings.json";
}

/// <summary>
/// Holds the runtime settings and persists them to a JSON file. On first run the file doesn't
/// exist, so settings are seeded from <c>appsettings.json</c> (the <c>Ollama</c> and
/// <c>Assistant</c> sections) and written out.
/// </summary>
public sealed class SettingsStore : ISettingsProvider
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AgentSettings _current;

    public SettingsStore(
        IOptions<SettingsFileOptions> fileOptions,
        IOptions<OllamaOptions> ollamaSeed,
        IOptions<AssistantOptions> assistantSeed,
        IHostEnvironment environment,
        ILogger<SettingsStore> logger)
    {
        _path = Path.GetFullPath(fileOptions.Value.FilePath, environment.ContentRootPath);
        _logger = logger;
        _current = Load() ?? SeedAndSave(ollamaSeed.Value, assistantSeed.Value);
    }

    public AgentSettings Current => Volatile.Read(ref _current);

    /// <summary>Persists and activates new settings. Callers validate first.</summary>
    public async Task SaveAsync(AgentSettings settings)
    {
        await _saveLock.WaitAsync();
        try
        {
            await WriteAtomicallyAsync(settings);
            Volatile.Write(ref _current, settings);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private AgentSettings? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("The settings file is empty.");
        }
        catch (JsonException ex)
        {
            // Keep the broken file for the operator to fix; run on defaults meanwhile.
            _logger.LogError(ex, "Settings file {Path} is invalid; using defaults until it is fixed or re-saved", _path);
            return new AgentSettings();
        }
    }

    private AgentSettings SeedAndSave(OllamaOptions ollama, AssistantOptions assistant)
    {
        var seeded = new AgentSettings
        {
            Assistant = new AssistantSettings
            {
                SystemPrompt = assistant.SystemPrompt,
                ToolInstructions = assistant.ToolInstructions,
                FarewellInstruction = assistant.FarewellInstruction,
                EndCallPhrases = assistant.EndCallPhrases,
                MaxHistoryMessages = assistant.MaxHistoryMessages,
                MaxToolRounds = assistant.MaxToolRounds,
            },
            Llm = new LlmSettings
            {
                Ollama = new OllamaSettings { BaseUrl = ollama.BaseUrl, Model = ollama.Model },
            },
        };
        try
        {
            WriteAtomicallyAsync(seeded).GetAwaiter().GetResult();
            _logger.LogInformation("Created settings file {Path} from appsettings.json", _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not create settings file {Path}; settings will not persist", _path);
        }
        return seeded;
    }

    private async Task WriteAtomicallyAsync(AgentSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Write-then-rename: a crash mid-write never leaves a truncated settings file behind.
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
