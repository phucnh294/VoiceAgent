using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Conversation;

/// <summary>A turn of the call as the browser sends it.</summary>
public sealed record ConversationMessage(string Role, string Content);

/// <summary>
/// Produces the assistant's reply to a call turn, running the tool loop:
/// model → (server tool → result → model)* → text, with client tools forwarded as actions.
/// Every model request, response and tool call is written to the call log.
/// </summary>
public sealed class ConversationService
{
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement;

    private readonly IChatModel _model;
    private readonly IReadOnlyDictionary<string, IAssistantTool> _tools;
    private readonly IReadOnlyList<ToolDefinition> _toolDefinitions;
    private readonly FarewellDetector _farewell;
    private readonly ICallLog _callLog;
    private readonly TimeProvider _time;
    private readonly AssistantOptions _options;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IChatModel model,
        IEnumerable<IAssistantTool> tools,
        FarewellDetector farewell,
        ICallLog callLog,
        TimeProvider time,
        IOptions<AssistantOptions> options,
        ILogger<ConversationService> logger)
    {
        _model = model;
        _tools = tools.ToDictionary(tool => tool.Definition.Name);
        _toolDefinitions = _tools.Values.Select(tool => tool.Definition).ToList();
        _farewell = farewell;
        _callLog = callLog;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    /// <exception cref="ChatModelException">The model failed.</exception>
    public async IAsyncEnumerable<ConversationEvent> StreamReplyAsync(
        Guid callId,
        IReadOnlyList<ConversationMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var turn = history.Count(message => message.Role == ChatMessage.User);
        var callerSaid = history[^1].Content;
        var callerIsLeaving = history[^1].Role == ChatMessage.User && _farewell.IsFarewell(callerSaid);
        var messages = BuildPrompt(history, callerIsLeaving);
        var toolNames = _toolDefinitions.Select(tool => tool.Name).ToList();

        var turnStarted = _time.GetTimestamp();
        var reply = new StringBuilder();
        var actions = new List<ActionLogEntry>();
        var completed = false;
        // State of the model request in flight, so a cut-short turn still logs its partial output.
        var round = 0;
        var roundText = new StringBuilder();
        var roundCalls = new List<ToolCallRequest>();
        var roundStarted = 0L;
        var roundLogged = true;

        try
        {
            for (round = 1; round <= _options.MaxToolRounds; round++)
            {
                roundText.Clear();
                roundCalls.Clear();
                roundStarted = _time.GetTimestamp();
                roundLogged = false;
                await _callLog.WriteAsync(callId, new ModelRequestRecord(turn, round, _model.ModelName, [.. messages], toolNames));

                await foreach (var item in _model.StreamChatAsync(messages, _toolDefinitions, ct))
                {
                    switch (item)
                    {
                        case TextDelta delta:
                            roundText.Append(delta.Text);
                            reply.Append(delta.Text);
                            yield return new TextEvent(delta.Text);
                            break;
                        case ToolCallRequest call:
                            roundCalls.Add(call);
                            break;
                    }
                }

                await LogModelResponseAsync(callId, turn, round, roundText, roundCalls, TurnStatus.Completed, roundStarted);
                roundLogged = true;

                if (roundCalls.Count == 0)
                {
                    break;
                }

                messages.Add(new ChatMessage(ChatMessage.Assistant, roundText.ToString()) { ToolCalls = ToChatToolCalls(roundCalls) });

                var needsFollowUp = false;
                foreach (var call in roundCalls)
                {
                    _logger.LogInformation("Call {CallId}: model called tool {Tool} (turn {Turn}, round {Round})", callId, call.Name, turn, round);

                    if (_tools.GetValueOrDefault(call.Name) is IClientTool)
                    {
                        await _callLog.WriteAsync(callId, new ToolCallRecord(turn, round, call.Name, "browser", call.Arguments, null, 0));
                        actions.Add(new ActionLogEntry(call.Name, call.Arguments, "model"));
                        yield return new ActionEvent(call.Name, call.Arguments);
                        continue;
                    }

                    var toolStarted = _time.GetTimestamp();
                    var result = await RunServerToolAsync(call, ct);
                    await _callLog.WriteAsync(callId, new ToolCallRecord(
                        turn, round, call.Name, "server", call.Arguments, result, ElapsedMs(toolStarted)));
                    messages.Add(new ChatMessage(ChatMessage.Tool, result) { ToolName = call.Name });
                    needsFollowUp = true;
                }

                // Only server tool results need the model to continue; a client action ends the reply.
                if (!needsFollowUp)
                {
                    break;
                }
            }

            if (callerIsLeaving && !actions.Any(action => action.Name == EndCallTool.Name))
            {
                _logger.LogInformation("Call {CallId}: caller said goodbye without the model calling {Tool}; ending call", callId, EndCallTool.Name);
                actions.Add(new ActionLogEntry(EndCallTool.Name, EmptyArguments, "farewell_detector"));
                yield return new ActionEvent(EndCallTool.Name, EmptyArguments);
            }

            completed = true;
        }
        finally
        {
            // Runs on success, on a model failure, and when the browser aborts (interrupt/hang-up).
            var status = completed ? TurnStatus.Completed
                : ct.IsCancellationRequested ? TurnStatus.Cancelled
                : TurnStatus.Failed;
            if (!roundLogged)
            {
                await LogModelResponseAsync(callId, turn, round, roundText, roundCalls, status, roundStarted);
            }
            var duration = ElapsedMs(turnStarted);
            await _callLog.WriteAsync(callId, new TurnRecord(
                turn, status, callerSaid, reply.ToString(), actions, Math.Min(round, _options.MaxToolRounds), duration));
            _logger.LogInformation("Call {CallId} turn {Turn}: {Status} in {DurationMs:F0} ms", callId, turn, status, duration);
        }
    }

    private List<ChatMessage> BuildPrompt(IReadOnlyList<ConversationMessage> history, bool callerIsLeaving)
    {
        var messages = new List<ChatMessage>();
        AddSystem(messages, _options.SystemPrompt);
        AddSystem(messages, _options.ToolInstructions);
        messages.AddRange(history
            .TakeLast(_options.MaxHistoryMessages)
            .Select(turn => new ChatMessage(turn.Role, turn.Content)));
        if (callerIsLeaving)
        {
            AddSystem(messages, _options.FarewellInstruction);
        }
        return messages;
    }

    private static void AddSystem(List<ChatMessage> messages, string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            messages.Add(new ChatMessage(ChatMessage.System, content));
        }
    }

    private async Task<string> RunServerToolAsync(ToolCallRequest call, CancellationToken ct)
    {
        if (_tools.GetValueOrDefault(call.Name) is not IServerTool tool)
        {
            _logger.LogWarning("Model called unknown tool {Tool}", call.Name);
            return $"Error: there is no tool named '{call.Name}'.";
        }

        try
        {
            return await tool.ExecuteAsync(call.Arguments, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tell the model instead of failing the turn, so it can apologise in its own words.
            _logger.LogError(ex, "Tool {Tool} failed", call.Name);
            return $"Error: the tool '{call.Name}' failed.";
        }
    }

    private Task LogModelResponseAsync(
        Guid callId, int turn, int round, StringBuilder text, List<ToolCallRequest> calls, TurnStatus status, long started) =>
        _callLog.WriteAsync(callId, new ModelResponseRecord(
            turn, round, text.ToString(), ToChatToolCalls(calls), status, ElapsedMs(started)));

    private static List<ChatToolCall> ToChatToolCalls(IEnumerable<ToolCallRequest> calls) =>
        calls.Select(call => new ChatToolCall(new ChatToolFunction(call.Name, call.Arguments))).ToList();

    private double ElapsedMs(long started) => _time.GetElapsedTime(started).TotalMilliseconds;
}
