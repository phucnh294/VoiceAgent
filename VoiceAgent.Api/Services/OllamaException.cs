namespace VoiceAgent.Api.Services;

/// <summary>The Ollama server was unreachable or returned an error.</summary>
public sealed class OllamaException : Exception
{
    public OllamaException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
