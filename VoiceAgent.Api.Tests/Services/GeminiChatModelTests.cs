using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Tests.Services;

public class GeminiChatModelTests
{
    private static readonly JsonElement Args = JsonDocument.Parse("""{"orderId":"A1"}""").RootElement;

    private static readonly ToolDefinition LookupTool = new(
        "lookup_order",
        "Looks up an order.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["orderId"] = new JsonObject { ["type"] = "string" } } });

    private static readonly ToolDefinition NoArgTool = new(
        "get_current_datetime", "Time.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

    [Fact]
    public void BuildRequest_SystemMessages_AreMergedIntoSystemInstruction()
    {
        var request = GeminiChatModel.BuildRequest(
            [new("system", "Persona."), new("system", "Tool rules."), new("user", "Hi"), new("system", "Say bye.")],
            []);

        Assert.Equal("Persona.\n\nTool rules.\n\nSay bye.", (string?)request["systemInstruction"]!["parts"]![0]!["text"]);
        var contents = request["contents"]!.AsArray();
        Assert.Single(contents);
        Assert.Equal("user", (string?)contents[0]!["role"]);
    }

    [Fact]
    public void BuildRequest_ConversationOpeningWithGreeting_GetsUserPlaceholderFirst()
    {
        var request = GeminiChatModel.BuildRequest([new("assistant", "Hello, thanks for calling."), new("user", "Hi")], []);

        var contents = request["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => (string?)c!["role"]));
        Assert.Equal(GeminiChatModel.CallStartedPlaceholder, (string?)contents[0]!["parts"]![0]!["text"]);
    }

    [Fact]
    public void BuildRequest_ToolRoundTrip_MapsCallAndResultWithSignature()
    {
        var call = new ChatToolCall(new ChatToolFunction("lookup_order", Args)) { Id = "c1", ThoughtSignature = "sig==" };
        var request = GeminiChatModel.BuildRequest(
            [
                new("user", "Where is order A1?"),
                new("assistant", "") { ToolCalls = [call] },
                new("tool", "Shipped") { ToolName = "lookup_order", ToolCallId = "c1" },
            ],
            [LookupTool]);

        var contents = request["contents"]!.AsArray();
        var modelPart = contents[1]!["parts"]![0]!;
        Assert.Equal("model", (string?)contents[1]!["role"]);
        Assert.Equal("lookup_order", (string?)modelPart["functionCall"]!["name"]);
        Assert.Equal("A1", (string?)modelPart["functionCall"]!["args"]!["orderId"]);
        Assert.Equal("sig==", (string?)modelPart["thoughtSignature"]);
        var response = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("user", (string?)contents[2]!["role"]);
        Assert.Equal("c1", (string?)response["id"]);
        Assert.Equal("Shipped", (string?)response["response"]!["result"]);
    }

    [Fact]
    public void BuildRequest_ConsecutiveToolResults_AreMergedIntoOneTurn()
    {
        var request = GeminiChatModel.BuildRequest(
            [
                new("user", "Q"),
                new("assistant", "") { ToolCalls = [new(new("a", Args)), new(new("b", Args))] },
                new("tool", "ra") { ToolName = "a" },
                new("tool", "rb") { ToolName = "b" },
            ],
            []);

        var contents = request["contents"]!.AsArray();
        Assert.Equal(3, contents.Count);
        Assert.Equal(2, contents[2]!["parts"]!.AsArray().Count);
    }

    [Fact]
    public void BuildRequest_Tools_UseJsonSchemaAndOmitEmptySchemas()
    {
        var request = GeminiChatModel.BuildRequest([new("user", "Hi")], [LookupTool, NoArgTool]);

        var declarations = request["tools"]![0]!["functionDeclarations"]!.AsArray();
        Assert.NotNull(declarations[0]!["parametersJsonSchema"]);
        Assert.Null(declarations[1]!["parametersJsonSchema"]);
    }

    [Fact]
    public void ParseChunk_TextAndFunctionCall_YieldsBothAndKeepsSignature()
    {
        var items = GeminiChatModel.ParseChunk("""
            {"candidates":[{"content":{"role":"model","parts":[
              {"text":"Let me check."},
              {"functionCall":{"id":"c9","name":"lookup_order","args":{"orderId":"A1"}},"thoughtSignature":"abc"}
            ]}}]}
            """).ToList();

        Assert.Equal(new TextDelta("Let me check."), items[0]);
        var call = Assert.IsType<ToolCallRequest>(items[1]);
        Assert.Equal("lookup_order", call.Name);
        Assert.Equal("c9", call.Id);
        Assert.Equal("abc", call.ThoughtSignature);
        Assert.Equal("A1", call.Arguments.GetProperty("orderId").GetString());
    }

    [Fact]
    public void ParseChunk_ThoughtParts_AreNotSpoken()
    {
        var items = GeminiChatModel.ParseChunk("""
            {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true},{"text":"Hello!"}]}}]}
            """);

        Assert.Equal([new TextDelta("Hello!")], items);
    }

    [Fact]
    public void ParseChunk_BlockedPrompt_Throws()
    {
        Assert.Throws<ChatModelException>(() =>
            GeminiChatModel.ParseChunk("""{"promptFeedback":{"blockReason":"SAFETY"}}""").ToList());
    }

    [Fact]
    public async Task StreamChat_SseResponse_StreamsItemsAndSendsKeyHeader()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hi \"}]}}]}\r\n\r\n" +
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"there.\"}]}}]}\r\n\r\n");
        var model = new GeminiChatModel(new HttpClient(handler), new GeminiSettings { ApiKey = "k-123", Model = "gemini-x" });

        var items = new List<ChatStreamItem>();
        await foreach (var item in model.StreamChatAsync([new("user", "Hi")], [], default))
        {
            items.Add(item);
        }

        Assert.Equal([new TextDelta("Hi "), new TextDelta("there.")], items);
        Assert.Equal("k-123", handler.LastRequest!.Headers.GetValues("x-goog-api-key").Single());
        Assert.EndsWith("models/gemini-x:streamGenerateContent?alt=sse", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("gemini:gemini-x", model.ModelName);
    }

    [Fact]
    public async Task StreamChat_InvalidKey_ThrowsWithApiMessage()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"error":{"message":"API key not valid."}}""");
        var model = new GeminiChatModel(new HttpClient(handler), new GeminiSettings { ApiKey = "bad", Model = "m" });

        var ex = await Assert.ThrowsAsync<ChatModelException>(async () =>
        {
            await foreach (var _ in model.StreamChatAsync([new("user", "Hi")], [], default))
            {
            }
        });
        Assert.Contains("API key not valid.", ex.Message);
    }

    [Fact]
    public async Task ListModels_KeepsOnlyChatModels()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"models":[
              {"name":"models/gemini-a","displayName":"Gemini A","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/embed-b","displayName":"Embed B","supportedGenerationMethods":["embedContent"]}
            ]}
            """);

        var models = await GeminiChatModel.ListModelsAsync(new HttpClient(handler), "k", default);

        var model = Assert.Single(models);
        Assert.Equal("gemini-a", model.Id);
        Assert.Equal("Gemini A (gemini-a)", model.DisplayName);
    }

    internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }
}
