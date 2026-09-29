using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly OllamaClient _ollamaClient;
    private readonly AssistantOptions _assistant;
    private readonly ILogger<ConversationController> _logger;

    public ConversationController(
        OllamaClient ollamaClient,
        IOptions<AssistantOptions> assistant,
        ILogger<ConversationController> logger)
    {
        _ollamaClient = ollamaClient;
        _assistant = assistant.Value;
        _logger = logger;
    }

    public sealed class ConversationRequest
    {
        /// <summary>The call so far, oldest first; the last entry is the caller's new turn.</summary>
        [Required, MinLength(1)]
        public List<ChatMessage> Messages { get; set; } = [];
    }

    /// <summary>
    /// Streams the assistant's reply as plain UTF-8 text fragments. The client owns the call
    /// history and sends it with every turn, so the API stays stateless.
    /// </summary>
    [HttpPost("stream")]
    public async Task<IActionResult> Stream([FromBody] ConversationRequest request, CancellationToken ct)
    {
        // The client may only speak as the caller or replay the assistant; the system prompt is ours.
        if (request.Messages.Any(m =>
                m.Role is not (ChatMessage.User or ChatMessage.Assistant) || string.IsNullOrWhiteSpace(m.Content)))
        {
            return BadRequest("Each message needs a role of 'user' or 'assistant' and non-empty content.");
        }

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(_assistant.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatMessage.System, _assistant.SystemPrompt));
        }
        messages.AddRange(request.Messages.TakeLast(_assistant.MaxHistoryMessages));

        try
        {
            await foreach (var token in _ollamaClient.StreamChatAsync(messages, ct))
            {
                if (!Response.HasStarted)
                {
                    Response.ContentType = "text/plain; charset=utf-8";
                    Response.Headers.CacheControl = "no-cache";
                }

                await Response.WriteAsync(token, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OllamaException ex) when (!Response.HasStarted)
        {
            _logger.LogError(ex, "Conversation turn failed before any reply was streamed");
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (OllamaException ex)
        {
            // Headers are already sent, so the caller just sees the reply stop early.
            _logger.LogError(ex, "Conversation turn failed mid-stream");
        }

        return new EmptyResult();
    }
}
