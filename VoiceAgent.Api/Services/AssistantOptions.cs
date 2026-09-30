namespace VoiceAgent.Api.Services;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Persona and style rules sent as the first message of every conversation.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// When to use which tool. Sent as a separate system message after the persona: small models
    /// follow tool rules noticeably more often that way than when they are buried in the persona.
    /// </summary>
    public string ToolInstructions { get; set; } = string.Empty;

    /// <summary>
    /// Added as the last system message when the caller is saying goodbye, so the model replies
    /// with a farewell instead of asking another question right before the call ends.
    /// </summary>
    public string FarewellInstruction { get; set; } =
        "The caller is ending the call. Reply with one short, warm goodbye sentence only, and do not ask anything.";

    /// <summary>Only the most recent turns are forwarded, to keep latency flat on long calls.</summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>Upper bound on model → server tool → model round trips within one reply.</summary>
    public int MaxToolRounds { get; set; } = 3;

    /// <summary>
    /// Phrases that end the call even if the model forgets to call <c>end_call</c>. Empty uses
    /// the built-in list in <c>FarewellDetector</c>.
    /// </summary>
    public List<string> EndCallPhrases { get; set; } = [];
}
