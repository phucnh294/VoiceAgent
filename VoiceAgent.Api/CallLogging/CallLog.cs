using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace VoiceAgent.Api.CallLogging;

public sealed class CallLogOptions
{
    public const string SectionName = "CallLog";

    public bool Enabled { get; set; } = true;

    /// <summary>Root folder for call logs; relative paths resolve against the API content root.</summary>
    public string Directory { get; set; } = "logs/calls";
}

/// <summary>Records everything that happens in a call, for later verification.</summary>
public interface ICallLog
{
    /// <summary>Appends a record. Never throws for I/O problems — a logging failure must not break the call.</summary>
    Task WriteAsync(Guid callId, CallLogRecord record);

    /// <summary>The call is over; releases any per-call state.</summary>
    void Complete(Guid callId);
}

/// <summary>
/// Writes one JSON Lines file per call: <c>{Directory}/{yyyy-MM-dd}/{callId}.jsonl</c>. The date
/// folder is fixed by the call's first record, so a call running past midnight stays in one file.
/// </summary>
public sealed class JsonlCallLog : ICallLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ILogger<JsonlCallLog> _logger;
    private readonly ConcurrentDictionary<Guid, string> _paths = new();
    // Turns of one call are sequential, but a call-end report can race the last turn's summary.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlCallLog(
        IOptions<CallLogOptions> options,
        IHostEnvironment environment,
        TimeProvider time,
        ILogger<JsonlCallLog> logger)
    {
        _root = Path.GetFullPath(options.Value.Directory, environment.ContentRootPath);
        _time = time;
        _logger = logger;
    }

    public async Task WriteAsync(Guid callId, CallLogRecord record)
    {
        var stamped = record with { Timestamp = _time.GetLocalNow(), CallId = callId };
        var path = _paths.GetOrAdd(callId, id =>
            Path.Combine(_root, stamped.Timestamp.ToString("yyyy-MM-dd"), $"{id}.jsonl"));
        var line = JsonSerializer.Serialize(stamped, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write call log {Path}", path);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Complete(Guid callId) => _paths.TryRemove(callId, out _);
}

/// <summary>Used when <c>CallLog:Enabled</c> is false.</summary>
public sealed class NullCallLog : ICallLog
{
    public Task WriteAsync(Guid callId, CallLogRecord record) => Task.CompletedTask;

    public void Complete(Guid callId)
    {
    }
}
