using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Conversation;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    public const string CallIdHeader = "X-Call-Id";

    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConversationService _conversation;
    private readonly ICallLog _callLog;
    private readonly ILogger<ConversationController> _logger;

    public ConversationController(
        ConversationService conversation,
        ICallLog callLog,
        ILogger<ConversationController> logger)
    {
        _conversation = conversation;
        _callLog = callLog;
        _logger = logger;
    }

    public sealed class ConversationRequest
    {
        /// <summary>Identifies the call in the call log. Optional; a new one is issued if missing.</summary>
        public Guid? CallId { get; set; }

        /// <summary>The call so far, oldest first; the last entry is the caller's new turn.</summary>
        [Required, MinLength(1)]
        public List<ConversationMessage> Messages { get; set; } = [];
    }

    public sealed class CallEndRequest
    {
        [Required, RegularExpression("caller|goodbye|idle|error|closed")]
        public string Reason { get; set; } = string.Empty;

        [Range(0, 24 * 60 * 60)]
        public int DurationSeconds { get; set; }

        /// <summary>The transcript as the caller saw it, including the greeting and interruptions.</summary>
        [MaxLength(500)]
        public List<TranscriptEntry> Transcript { get; set; } = [];
    }

    /// <summary>
    /// Streams the assistant's reply as NDJSON events (see <see cref="ConversationEvent"/>). The
    /// client owns the call history and sends it with every turn, so the API stays stateless.
    /// The call ID used for logging is echoed in the <c>X-Call-Id</c> response header.
    /// </summary>
    [HttpPost("stream")]
    public async Task<IActionResult> Stream([FromBody] ConversationRequest request, CancellationToken ct)
    {
        // The client may only speak as the caller or replay the assistant; system and tool
        // messages are ours.
        if (request.Messages.Any(m =>
                m.Role is not (ChatMessage.User or ChatMessage.Assistant) || string.IsNullOrWhiteSpace(m.Content)))
        {
            return BadRequest("Each message needs a role of 'user' or 'assistant' and non-empty content.");
        }

        var callId = request.CallId ?? Guid.NewGuid();
        Response.Headers[CallIdHeader] = callId.ToString();
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        try
        {
            await foreach (var evt in _conversation.StreamReplyAsync(callId, request.Messages, ct))
            {
                await WriteEventAsync(evt, ct);
            }
        }
        catch (ChatModelException ex) when (!Response.HasStarted)
        {
            _logger.LogError(ex, "Call {CallId}: turn failed before any reply was streamed", callId);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (ChatModelException ex)
        {
            _logger.LogError(ex, "Call {CallId}: turn failed mid-stream", callId);
            await WriteEventAsync(new ErrorEvent("The assistant stopped responding."), ct);
        }

        return new EmptyResult();
    }

    /// <summary>The browser reports the end of a call so the call log records why and how it ended.</summary>
    [HttpPost("{callId:guid}/end")]
    public async Task<IActionResult> End(Guid callId, [FromBody] CallEndRequest request)
    {
        await _callLog.WriteAsync(callId, new CallEndRecord(request.Reason, request.DurationSeconds, request.Transcript));
        _callLog.Complete(callId);
        _logger.LogInformation("Call {CallId} ended ({Reason}) after {DurationSeconds}s", callId, request.Reason, request.DurationSeconds);
        return NoContent();
    }

    private async Task WriteEventAsync(ConversationEvent evt, CancellationToken ct)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(evt, EventJsonOptions) + "\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
