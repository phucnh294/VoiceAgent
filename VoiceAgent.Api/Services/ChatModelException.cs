namespace VoiceAgent.Api.Services;

/// <summary>The chat model was unreachable or returned an error.</summary>
public sealed class ChatModelException : Exception
{
    public ChatModelException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
