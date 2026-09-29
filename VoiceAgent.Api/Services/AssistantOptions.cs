namespace VoiceAgent.Api.Services;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Persona and style rules sent as the first message of every conversation.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Only the most recent turns are forwarded, to keep latency flat on long calls.</summary>
    public int MaxHistoryMessages { get; set; } = 20;
}
