using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Conversation;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Tests.Conversation;

public class ConversationServiceTests
{
    private const string ToolInstructions = "Use tools.";
    private const string FarewellInstruction = "Say goodbye only.";

    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task StreamReply_PlainAnswer_StreamsTextOnly()
    {
        var model = new ScriptedChatModel([new TextDelta("Hello "), new TextDelta("there.")]);

        var events = await RunAsync(model, "Hi");

        Assert.Equal([new TextEvent("Hello "), new TextEvent("there.")], events);
        Assert.Equal(ChatMessage.System, model.Requests[0][0].Role);
    }

    [Fact]
    public async Task StreamReply_ServerToolCall_FeedsResultBackToModel()
    {
        var model = new ScriptedChatModel(
            [new ToolCallRequest(StubServerTool.ToolName, NoArguments)],
            [new TextDelta("It is noon.")]);

        var events = await RunAsync(model, "What time is it?");

        Assert.Equal([new TextEvent("It is noon.")], events);
        Assert.Equal(2, model.Requests.Count);
        var toolResult = model.Requests[1][^1];
        Assert.Equal(ChatMessage.Tool, toolResult.Role);
        Assert.Equal(StubServerTool.ToolName, toolResult.ToolName);
        Assert.Equal("12:00", toolResult.Content);
        Assert.NotNull(model.Requests[1][^2].ToolCalls);
    }

    [Fact]
    public async Task StreamReply_EndCallTool_ForwardsActionWithoutAnotherRound()
    {
        var farewell = JsonDocument.Parse("""{"farewell":"Goodbye!"}""").RootElement;
        var model = new ScriptedChatModel([new ToolCallRequest(EndCallTool.Name, farewell)]);

        var events = await RunAsync(model, "I'd like to finish, thank you");

        var action = Assert.IsType<ActionEvent>(Assert.Single(events));
        Assert.Equal(EndCallTool.Name, action.Name);
        Assert.Equal("Goodbye!", action.Arguments.GetProperty("farewell").GetString());
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task StreamReply_CallerSaysByeButModelSkipsTool_EndsCallAnyway()
    {
        var model = new ScriptedChatModel([new TextDelta("Bye!")]);

        var events = await RunAsync(model, "Okay thanks, bye");

        Assert.Equal(new TextEvent("Bye!"), events[0]);
        Assert.Equal(EndCallTool.Name, Assert.IsType<ActionEvent>(events[1]).Name);
    }

    [Fact]
    public async Task StreamReply_CallerSaysBye_AddsFarewellInstructionLast()
    {
        var model = new ScriptedChatModel([new TextDelta("Goodbye!")]);

        await RunAsync(model, "that's all, bye");

        var last = model.Requests[0][^1];
        Assert.Equal(ChatMessage.System, last.Role);
        Assert.Equal(FarewellInstruction, last.Content);
    }

    [Fact]
    public async Task StreamReply_OrdinaryTurn_SendsPersonaThenToolRulesThenHistory()
    {
        var model = new ScriptedChatModel([new TextDelta("Sure.")]);

        await RunAsync(model, "Can you help me?");

        Assert.Equal(
            ["You are a test assistant.", ToolInstructions, "Can you help me?"],
            model.Requests[0].Select(message => message.Content));
    }

    [Fact]
    public async Task StreamReply_ModelCallsEndCallOnFarewell_SendsSingleAction()
    {
        var model = new ScriptedChatModel([new ToolCallRequest(EndCallTool.Name, NoArguments)]);

        var events = await RunAsync(model, "bye");

        Assert.Single(events.OfType<ActionEvent>());
    }

    [Fact]
    public async Task StreamReply_UnknownTool_TellsModelInsteadOfFailing()
    {
        var model = new ScriptedChatModel(
            [new ToolCallRequest("book_flight", NoArguments)],
            [new TextDelta("Sorry, I can't do that.")]);

        var events = await RunAsync(model, "Book me a flight");

        Assert.Equal([new TextEvent("Sorry, I can't do that.")], events);
        Assert.StartsWith("Error:", model.Requests[1][^1].Content);
    }

    [Fact]
    public async Task StreamReply_ModelKeepsCallingTools_StopsAtMaxRounds()
    {
        var loop = Enumerable.Range(0, 10)
            .Select(_ => (IReadOnlyList<ChatStreamItem>)[new ToolCallRequest(StubServerTool.ToolName, NoArguments)])
            .ToArray();
        var model = new ScriptedChatModel(loop);

        await RunAsync(model, "What time is it?", maxToolRounds: 3);

        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public async Task StreamReply_Always_LogsFullPromptIncludingSystemPrompt()
    {
        var model = new ScriptedChatModel([new TextDelta("Sure.")]);
        var log = new RecordingCallLog();

        await RunAsync(model, "Can you help me?", log: log);

        var request = Assert.IsType<ModelRequestRecord>(log.Records[0]);
        Assert.Equal(1, request.Turn);
        Assert.Equal("test-model", request.Model);
        Assert.Equal(ChatMessage.System, request.Messages[0].Role);
        Assert.Equal("You are a test assistant.", request.Messages[0].Content);
        Assert.Equal("Can you help me?", request.Messages[^1].Content);
        Assert.Contains(EndCallTool.Name, request.Tools);
        Assert.All(log.Records, record => Assert.Equal(CallId, log.CallIdOf(record)));
    }

    [Fact]
    public async Task StreamReply_ServerTool_LogsRequestResponseToolAndTurnInOrder()
    {
        var model = new ScriptedChatModel(
            [new ToolCallRequest(StubServerTool.ToolName, NoArguments)],
            [new TextDelta("It is noon.")]);
        var log = new RecordingCallLog();

        await RunAsync(model, "What time is it?", log: log);

        Assert.Equal(
            [typeof(ModelRequestRecord), typeof(ModelResponseRecord), typeof(ToolCallRecord),
             typeof(ModelRequestRecord), typeof(ModelResponseRecord), typeof(TurnRecord)],
            log.Records.Select(record => record.GetType()));
        var tool = log.Records.OfType<ToolCallRecord>().Single();
        Assert.Equal("server", tool.RunsOn);
        Assert.Equal("12:00", tool.Result);
        var turn = log.Records.OfType<TurnRecord>().Single();
        Assert.Equal(TurnStatus.Completed, turn.Status);
        Assert.Equal("What time is it?", turn.CallerSaid);
        Assert.Equal("It is noon.", turn.AssistantReplied);
        Assert.Equal(2, turn.ModelRounds);
    }

    [Fact]
    public async Task StreamReply_FarewellBackstop_LogsActionSource()
    {
        var model = new ScriptedChatModel([new TextDelta("Bye!")]);
        var log = new RecordingCallLog();

        await RunAsync(model, "okay bye", log: log);

        var action = Assert.Single(log.Records.OfType<TurnRecord>().Single().Actions);
        Assert.Equal(EndCallTool.Name, action.Name);
        Assert.Equal("farewell_detector", action.Source);
    }

    [Fact]
    public async Task StreamReply_CallerInterrupts_LogsPartialReplyAsCancelled()
    {
        var model = new ScriptedChatModel([new TextDelta("We are open "), new TextDelta("from nine.")]);
        var log = new RecordingCallLog();
        using var interrupt = new CancellationTokenSource();
        var service = CreateService(model, log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.StreamReplyAsync(
                CallId, [new ConversationMessage("user", "When are you open?")], interrupt.Token))
            {
                interrupt.Cancel(); // The caller cuts in after the first fragment.
            }
        });

        var response = log.Records.OfType<ModelResponseRecord>().Single();
        Assert.Equal(TurnStatus.Cancelled, response.Status);
        Assert.Equal("We are open ", response.Text);
        Assert.Equal(TurnStatus.Cancelled, log.Records.OfType<TurnRecord>().Single().Status);
    }

    private static readonly Guid CallId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static async Task<List<ConversationEvent>> RunAsync(
        ScriptedChatModel model, string callerSays, int maxToolRounds = 3, RecordingCallLog? log = null)
    {
        var service = CreateService(model, log ?? new RecordingCallLog(), maxToolRounds);

        var events = new List<ConversationEvent>();
        await foreach (var evt in service.StreamReplyAsync(CallId, [new ConversationMessage("user", callerSays)], default))
        {
            events.Add(evt);
        }
        return events;
    }

    private static ConversationService CreateService(ScriptedChatModel model, ICallLog log, int maxToolRounds = 3)
    {
        var settings = new AgentSettings
        {
            Assistant = new AssistantSettings
            {
                SystemPrompt = "You are a test assistant.",
                ToolInstructions = ToolInstructions,
                FarewellInstruction = FarewellInstruction,
                MaxToolRounds = maxToolRounds,
            },
        };
        return new ConversationService(
            new FixedModelResolver(model),
            new FixedToolRegistry([new EndCallTool(), new StubServerTool()]),
            new FarewellDetector(),
            log,
            TimeProvider.System,
            new FixedSettings(settings),
            NullLogger<ConversationService>.Instance);
    }

    private sealed class FixedSettings(AgentSettings settings) : ISettingsProvider
    {
        public AgentSettings Current => settings;
    }

    private sealed class FixedModelResolver(IChatModel model) : IChatModelResolver
    {
        public IChatModel Resolve(LlmSettings settings) => model;
    }

    private sealed class FixedToolRegistry(IReadOnlyList<IAssistantTool> tools) : IToolRegistry
    {
        public IReadOnlyList<IAssistantTool> Build(ToolSettings settings, Guid callId) => tools;
    }

    /// <summary>Keeps call log records in memory, in write order.</summary>
    private sealed class RecordingCallLog : ICallLog
    {
        private readonly Dictionary<CallLogRecord, Guid> _callIds = new(ReferenceEqualityComparer.Instance);

        public List<CallLogRecord> Records { get; } = [];

        public Guid CallIdOf(CallLogRecord record) => _callIds[record];

        public Task WriteAsync(Guid callId, CallLogRecord record)
        {
            Records.Add(record);
            _callIds[record] = callId;
            return Task.CompletedTask;
        }

        public void Complete(Guid callId)
        {
        }
    }

    /// <summary>Replays one scripted response per model call and records every prompt.</summary>
    private sealed class ScriptedChatModel(params IReadOnlyList<ChatStreamItem>[] responses) : IChatModel
    {
        private int _next;

        public string ModelName => "test-model";

        public List<List<ChatMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<ChatStreamItem> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add([.. messages]);
            foreach (var item in responses[_next++])
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested(); // Like the HTTP stream read in OllamaClient.
                yield return item;
            }
        }
    }

    private sealed class StubServerTool : IServerTool
    {
        public const string ToolName = "stub_time";

        public ToolDefinition Definition { get; } = new(ToolName, "Returns a fixed time.", new());

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult("12:00");
    }
}
