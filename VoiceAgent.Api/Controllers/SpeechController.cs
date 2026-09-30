using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Controllers;

/// <summary>Voice for the call page: speech synthesis and the non-secret settings a call needs.</summary>
[ApiController]
[Route("api")]
public class SpeechController : ControllerBase
{
    private readonly ISettingsProvider _settings;
    private readonly KokoroClient _kokoro;
    private readonly ILogger<SpeechController> _logger;

    public SpeechController(ISettingsProvider settings, KokoroClient kokoro, ILogger<SpeechController> logger)
    {
        _settings = settings;
        _kokoro = kokoro;
        _logger = logger;
    }

    public sealed class SpeechRequest
    {
        [Required, MaxLength(1000)]
        public string Text { get; set; } = string.Empty;

        /// <summary>Preview overrides (the Settings "Test voice" button); null uses the saved setting.</summary>
        public string? Voice { get; set; }

        [Range(0.5, 2.0)]
        public double? Speed { get; set; }
    }

    /// <summary>Greeting and voice choice for the call page. Contains no secrets.</summary>
    [HttpGet("call-settings")]
    public object CallSettings()
    {
        var settings = _settings.Current;
        return new
        {
            greeting = settings.Assistant.Greeting,
            voice = new
            {
                provider = settings.Voice.Provider,
                browserVoiceName = settings.Voice.Browser.VoiceName,
                browserRate = settings.Voice.Browser.Rate,
            },
        };
    }

    /// <summary>Synthesizes one sentence with Kokoro and streams it back as MP3.</summary>
    [HttpPost("speech")]
    public async Task<IActionResult> Speech([FromBody] SpeechRequest request, CancellationToken ct)
    {
        var kokoro = _settings.Current.Voice.Kokoro;
        try
        {
            var upstream = await _kokoro.SynthesizeAsync(
                kokoro.BaseUrl, request.Text, request.Voice ?? kokoro.Voice, request.Speed ?? kokoro.Speed, ct);
            HttpContext.Response.RegisterForDispose(upstream);
            return File(await upstream.Content.ReadAsStreamAsync(ct), "audio/mpeg");
        }
        catch (SpeechException ex)
        {
            _logger.LogWarning("Speech synthesis failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
