using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;
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

    private readonly IChatModelResolver _models;
    private readonly IToolRegistry _toolRegistry;
    private readonly FarewellDetector _farewell;
    private readonly ICallLog _callLog;
    private readonly TimeProvider _time;
    private readonly ISettingsProvider _settings;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IChatModelResolver models,
        IToolRegistry toolRegistry,
        FarewellDetector farewell,
        ICallLog callLog,
        TimeProvider time,
        ISettingsProvider settings,
        ILogger<ConversationService> logger)
    {
        _models = models;
        _toolRegistry = toolRegistry;
        _farewell = farewell;
        _callLog = callLog;
        _time = time;
        _settings = settings;
        _logger = logger;
    }

    /// <exception cref="ChatModelException">The model failed.</exception>
    public async IAsyncEnumerable<ConversationEvent> StreamReplyAsync(
        Guid callId,
        IReadOnlyList<ConversationMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // One snapshot per turn: a settings save mid-turn applies from the next turn.
        var settings = _settings.Current;
        var options = settings.Assistant;
        var model = _models.Resolve(settings.Llm);
        var tools = _toolRegistry.Build(settings.Tools, callId).ToDictionary(tool => tool.Definition.Name);
        var toolDefinitions = tools.Values.Select(tool => tool.Definition).ToList();

        var turn = history.Count(message => message.Role == ChatMessage.User);
        var callerSaid = history[^1].Content;
        var callerIsLeaving = history[^1].Role == ChatMessage.User
            && tools.ContainsKey(EndCallTool.Name)
            && _farewell.IsFarewell(callerSaid, options.EndCallPhrases);
        var messages = BuildPrompt(options, history, callerIsLeaving);
        var toolNames = toolDefinitions.Select(tool => tool.Name).ToList();

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
            for (round = 1; round <= options.MaxToolRounds; round++)
            {
                roundText.Clear();
                roundCalls.Clear();
                roundStarted = _time.GetTimestamp();
                roundLogged = false;
                await _callLog.WriteAsync(callId, new ModelRequestRecord(turn, round, model.ModelName, [.. messages], toolNames));

                await foreach (var item in model.StreamChatAsync(messages, toolDefinitions, ct))
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

                    if (tools.GetValueOrDefault(call.Name) is IClientTool)
                    {
                        await _callLog.WriteAsync(callId, new ToolCallRecord(turn, round, call.Name, "browser", call.Arguments, null, 0));
                        actions.Add(new ActionLogEntry(call.Name, call.Arguments, "model"));
                        yield return new ActionEvent(call.Name, call.Arguments);
                        continue;
                    }

                    var toolStarted = _time.GetTimestamp();
                    var result = await RunServerToolAsync(tools, call, ct);
                    await _callLog.WriteAsync(callId, new ToolCallRecord(
                        turn, round, call.Name, "server", call.Arguments, result, ElapsedMs(toolStarted)));
                    messages.Add(new ChatMessage(ChatMessage.Tool, result) { ToolName = call.Name, ToolCallId = call.Id });
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
                turn, status, callerSaid, reply.ToString(), actions, Math.Min(round, options.MaxToolRounds), duration));
            _logger.LogInformation("Call {CallId} turn {Turn}: {Status} in {DurationMs:F0} ms", callId, turn, status, duration);
        }
    }

    private static List<ChatMessage> BuildPrompt(
        AssistantSettings options, IReadOnlyList<ConversationMessage> history, bool callerIsLeaving)
    {
        var messages = new List<ChatMessage>();
        AddSystem(messages, options.SystemPrompt);
        AddSystem(messages, options.ToolInstructions);
        messages.AddRange(history
            .TakeLast(options.MaxHistoryMessages)
            .Select(turn => new ChatMessage(turn.Role, turn.Content)));
        if (callerIsLeaving)
        {
            AddSystem(messages, options.FarewellInstruction);
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

    private async Task<string> RunServerToolAsync(
        IReadOnlyDictionary<string, IAssistantTool> tools, ToolCallRequest call, CancellationToken ct)
    {
        if (tools.GetValueOrDefault(call.Name) is not IServerTool tool)
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
        calls.Select(call => new ChatToolCall(new ChatToolFunction(call.Name, call.Arguments))
        {
            Id = call.Id,
            ThoughtSignature = call.ThoughtSignature,
        }).ToList();

    private double ElapsedMs(long started) => _time.GetElapsedTime(started).TotalMilliseconds;
}
