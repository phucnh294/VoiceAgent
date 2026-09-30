using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Services;

/// <summary>Creates the chat model for a turn, so a provider/model change applies from the next turn.</summary>
public sealed class ChatModelResolver : IChatModelResolver
{
    public const string OllamaClientName = "ollama";
    public const string GeminiClientName = "gemini";

    private readonly IHttpClientFactory _httpFactory;

    public ChatModelResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public IChatModel Resolve(LlmSettings settings) => settings.Provider switch
    {
        LlmProviders.Gemini => new GeminiChatModel(_httpFactory.CreateClient(GeminiClientName), settings.Gemini),
        _ => new OllamaClient(_httpFactory.CreateClient(OllamaClientName), settings.Ollama),
    };
}
