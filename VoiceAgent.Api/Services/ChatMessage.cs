namespace VoiceAgent.Api.Services;

public sealed record ChatMessage(string Role, string Content)
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}
