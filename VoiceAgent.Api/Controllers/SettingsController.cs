using Microsoft.AspNetCore.Mvc;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Controllers;

/// <summary>Runtime settings for the Settings panel. Loopback-only: it can read and change API keys.</summary>
[ApiController]
[Route("api/settings")]
[LocalOnly]
public class SettingsController : ControllerBase
{
    private readonly SettingsStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly KokoroClient _kokoro;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        SettingsStore store,
        IHttpClientFactory httpFactory,
        KokoroClient kokoro,
        ILogger<SettingsController> logger)
    {
        _store = store;
        _httpFactory = httpFactory;
        _kokoro = kokoro;
        _logger = logger;
    }

    public sealed record ModelListRequest(string Provider, string? BaseUrl, string? ApiKey);

    [HttpGet]
    public SettingsView Get() => SettingsMapper.ToView(_store.Current);

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SettingsView update)
    {
        var next = SettingsMapper.Apply(_store.Current, update);
        var errors = SettingsValidator.Validate(next);
        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }
        await _store.SaveAsync(next);
        _logger.LogInformation(
            "Settings saved: model {Provider}, voice {Voice}, {WebhookCount} webhook tool(s)",
            next.Llm.Provider, next.Voice.Provider, next.Tools.Webhooks.Count);
        return Ok(SettingsMapper.ToView(next));
    }

    /// <summary>
    /// Lists models for the model dropdown. Uses the base URL / API key in the request when given
    /// (to try them before saving), otherwise the saved ones.
    /// </summary>
    [HttpPost("models")]
    public async Task<IActionResult> Models([FromBody] ModelListRequest request, CancellationToken ct)
    {
        var llm = _store.Current.Llm;
        try
        {
            IReadOnlyList<ModelInfo> models = request.Provider switch
            {
                LlmProviders.Ollama => await OllamaClient.ListModelsAsync(
                    _httpFactory.CreateClient(ChatModelResolver.OllamaClientName),
                    string.IsNullOrWhiteSpace(request.BaseUrl) ? llm.Ollama.BaseUrl : request.BaseUrl, ct),
                LlmProviders.Gemini when (string.IsNullOrWhiteSpace(request.ApiKey) ? llm.Gemini.ApiKey : request.ApiKey) is { Length: > 0 } key =>
                    await GeminiChatModel.ListModelsAsync(_httpFactory.CreateClient(ChatModelResolver.GeminiClientName), key, ct),
                LlmProviders.Gemini => throw new ChatModelException("Enter a Gemini API key first."),
                _ => throw new ChatModelException($"Unknown provider '{request.Provider}'."),
            };
            return Ok(new { models });
        }
        catch (ChatModelException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpGet("voices")]
    public async Task<IActionResult> Voices([FromQuery] string? baseUrl, CancellationToken ct)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(baseUrl) ? _store.Current.Voice.Kokoro.BaseUrl : baseUrl;
            return Ok(new { voices = await _kokoro.ListVoicesAsync(url, ct) });
        }
        catch (SpeechException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
