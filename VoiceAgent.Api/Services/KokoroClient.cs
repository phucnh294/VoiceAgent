using System.Net.Http.Json;
using System.Text.Json;

namespace VoiceAgent.Api.Services;

/// <summary>The Kokoro TTS server's OpenAI-compatible speech API (Kokoro-FastAPI).</summary>
public sealed class KokoroClient
{
    public const string HttpClientName = "kokoro";

    private readonly HttpClient _http;

    public KokoroClient(IHttpClientFactory httpFactory)
    {
        _http = httpFactory.CreateClient(HttpClientName);
    }

    /// <summary>Starts synthesis; the caller streams and disposes the response.</summary>
    /// <exception cref="SpeechException">Kokoro is unreachable or rejected the request.</exception>
    public async Task<HttpResponseMessage> SynthesizeAsync(
        string baseUrl, string text, string voice, double speed, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl, "v1/audio/speech"))
        {
            Content = JsonContent.Create(new
            {
                model = "kokoro",
                input = text,
                voice,
                speed,
                response_format = "mp3",
            }),
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechException($"Kokoro is unreachable at {baseUrl}. Is the TTS container running?", ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new SpeechException($"Kokoro returned {(int)response.StatusCode}: {body}");
        }
        return response;
    }

    /// <exception cref="SpeechException">Kokoro is unreachable or returned an unexpected body.</exception>
    public async Task<IReadOnlyList<string>> ListVoicesAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(Endpoint(baseUrl, "v1/audio/voices"), ct));
            return doc.RootElement.GetProperty("voices").EnumerateArray()
                .Select(voice => voice.ValueKind == JsonValueKind.String ? voice.GetString()! : voice.GetProperty("id").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new SpeechException($"Could not list Kokoro voices at {baseUrl}: {ex.Message}", ex);
        }
    }

    private static Uri Endpoint(string baseUrl, string path) => new(new Uri(baseUrl.TrimEnd('/') + "/"), path);
}

/// <summary>The text-to-speech server failed.</summary>
public sealed class SpeechException : Exception
{
    public SpeechException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
