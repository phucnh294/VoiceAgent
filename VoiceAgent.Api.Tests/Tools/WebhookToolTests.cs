using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

namespace VoiceAgent.Api.Tests.Tools;

public class WebhookToolTests
{
    private static readonly JsonElement Args = JsonDocument.Parse("""{"orderId":"A1"}""").RootElement;
    private static readonly Guid CallId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task Execute_Success_PostsContractAndReturnsBody()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, "Order A1 shipped.")));
        var tool = CreateTool(handler, headers: new() { ["Authorization"] = "Bearer t" });

        var result = await tool.ExecuteAsync(Args, default);

        Assert.Equal("Order A1 shipped.", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("Bearer t", handler.Request.Headers.GetValues("Authorization").Single());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("lookup_order", body.RootElement.GetProperty("tool").GetString());
        Assert.Equal("A1", body.RootElement.GetProperty("arguments").GetProperty("orderId").GetString());
        Assert.Equal(CallId, body.RootElement.GetProperty("callId").GetGuid());
    }

    [Fact]
    public async Task Execute_ServerError_ReturnsErrorForTheModel()
    {
        var tool = CreateTool(new RecordingHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.InternalServerError, "boom"))));

        var result = await tool.ExecuteAsync(Args, default);

        Assert.StartsWith("Error: the tool returned HTTP 500.", result);
    }

    [Fact]
    public async Task Execute_SlowServer_TimesOutWithErrorForTheModel()
    {
        var tool = CreateTool(
            new RecordingHandler(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return Respond(HttpStatusCode.OK, "late");
            }),
            timeoutSeconds: 1);

        var result = await tool.ExecuteAsync(Args, default);

        Assert.Equal("Error: the tool did not answer within 1 seconds.", result);
    }

    [Fact]
    public async Task Execute_HugeResponse_IsTruncated()
    {
        var tool = CreateTool(new RecordingHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, new string('x', 10_000)))));

        var result = await tool.ExecuteAsync(Args, default);

        Assert.StartsWith(new string('x', WebhookTool.MaxResultChars), result);
        Assert.EndsWith("…(truncated)", result);
    }

    [Fact]
    public async Task Execute_Unreachable_ReturnsErrorForTheModel()
    {
        var tool = CreateTool(new RecordingHandler((_, _) => throw new HttpRequestException("connection refused")));

        var result = await tool.ExecuteAsync(Args, default);

        Assert.Equal("Error: the tool could not be reached (connection refused).", result);
    }

    [Fact]
    public void Definition_WithoutSchema_UsesEmptyObjectSchema()
    {
        var tool = CreateTool(new RecordingHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, ""))), parameters: null);

        Assert.Equal("object", (string?)tool.Definition.Parameters["type"]);
    }

    private static WebhookTool CreateTool(
        RecordingHandler handler,
        int timeoutSeconds = 10,
        Dictionary<string, string>? headers = null,
        JsonObject? parameters = null) =>
        new(
            new WebhookToolSettings
            {
                Name = "lookup_order",
                Description = "Looks up an order.",
                Url = "https://hooks.test/orders",
                TimeoutSeconds = timeoutSeconds,
                Headers = headers ?? [],
                Parameters = parameters,
            },
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            CallId);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request, ct);
        }
    }
}
