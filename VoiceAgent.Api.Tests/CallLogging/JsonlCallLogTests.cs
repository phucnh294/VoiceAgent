using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.Tests.CallLogging;

public sealed class JsonlCallLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"calllog-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteAsync_TwoRecords_AppendsJsonLinesToOneFilePerCall()
    {
        var log = CreateLog();
        var callId = Guid.NewGuid();

        await log.WriteAsync(callId, new ModelRequestRecord(
            1, 1, "qwen", [new ChatMessage("system", "Be brief."), new ChatMessage("user", "Hi")], ["end_call"]));
        await log.WriteAsync(callId, new CallEndRecord("goodbye", 42, [new TranscriptEntry("user", "Hi", Typed: true)]));

        var file = Assert.Single(Directory.GetFiles(_root, "*.jsonl", SearchOption.AllDirectories));
        Assert.Equal($"{callId}.jsonl", Path.GetFileName(file));
        var lines = await File.ReadAllLinesAsync(file);
        Assert.Equal(2, lines.Length);

        using var request = JsonDocument.Parse(lines[0]);
        Assert.Equal("model_request", request.RootElement.GetProperty("type").GetString());
        Assert.Equal(callId, request.RootElement.GetProperty("callId").GetGuid());
        Assert.Equal("Be brief.", request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        using var end = JsonDocument.Parse(lines[1]);
        Assert.Equal("call_end", end.RootElement.GetProperty("type").GetString());
        Assert.True(end.RootElement.GetProperty("transcript")[0].GetProperty("typed").GetBoolean());
    }

    [Fact]
    public async Task WriteAsync_TurnStatus_IsWrittenInLowercase()
    {
        var log = CreateLog();

        await log.WriteAsync(Guid.NewGuid(), new TurnRecord(1, TurnStatus.Cancelled, "Hi", "Hel", [], 1, 10));

        var line = (await File.ReadAllLinesAsync(Directory.GetFiles(_root, "*.jsonl", SearchOption.AllDirectories)[0]))[0];
        using var turn = JsonDocument.Parse(line);
        Assert.Equal("cancelled", turn.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WriteAsync_DifferentCalls_WriteSeparateFiles()
    {
        var log = CreateLog();

        await log.WriteAsync(Guid.NewGuid(), new CallEndRecord("idle", 60, []));
        await log.WriteAsync(Guid.NewGuid(), new CallEndRecord("caller", 5, []));

        Assert.Equal(2, Directory.GetFiles(_root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private JsonlCallLog CreateLog() => new(
        Options.Create(new CallLogOptions { Directory = _root }),
        new StubHostEnvironment(),
        TimeProvider.System,
        NullLogger<JsonlCallLog>.Instance);

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
