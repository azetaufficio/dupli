using System.Collections.Concurrent;
using Dupli.Contracts.Logs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Dupli.Agent.Server;

/// <summary>
/// Serilog sink buffering Information+ events of the agent's own code for upload. Bounded: when the
/// server is unreachable for long, the oldest events are dropped and counted (full logs stay on disk).
/// </summary>
public sealed class ServerLogBuffer : ILogEventSink
{
    public const int Capacity = 5000;

    private readonly ConcurrentQueue<AgentLogEntryDto> _queue = new();
    private int _dropped;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information || !IsAgentSource(logEvent))
            return;

        _queue.Enqueue(new AgentLogEntryDto
        {
            Timestamp = logEvent.Timestamp.ToUniversalTime(),
            Level = logEvent.Level.ToString(),
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString(),
            JobId = Scalar(logEvent, "JobId"),
            Properties = logEvent.Properties
                .Where(p => p.Key is "PolicyId" or "SourceId" or "Item" or "RunId")
                .ToDictionary(p => p.Key, p => p.Value is ScalarValue { Value: { } v } ? v.ToString() ?? "" : p.Value.ToString()),
        });

        while (_queue.Count > Capacity && _queue.TryDequeue(out _))
            Interlocked.Increment(ref _dropped);
    }

    public (List<AgentLogEntryDto> Entries, int Dropped) Take(int max)
    {
        var entries = new List<AgentLogEntryDto>(Math.Min(max, _queue.Count));
        while (entries.Count < max && _queue.TryDequeue(out var e))
            entries.Add(e);
        return (entries, Interlocked.Exchange(ref _dropped, 0));
    }

    // Upload traffic itself (HttpClient, the uploader) must not feed back into the buffer.
    private static bool IsAgentSource(LogEvent e) =>
        Scalar(e, "SourceContext") is not { } source
        || (source.StartsWith("Dupli.", StringComparison.Ordinal) && source != typeof(ServerLogUploader).FullName);

    private static string? Scalar(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) && v is ScalarValue { Value: { } value } ? value.ToString() : null;
}

public sealed class ServerLogUploader(ServerLogBuffer buffer, ServerClient server, TimeProvider time, ILogger<ServerLogUploader> logger)
    : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private (List<AgentLogEntryDto> Entries, int Dropped)? _unsent;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await FlushAsync(stoppingToken);
        }

        // Best effort on shutdown.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(cts.Token);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        while (true)
        {
            var batch = _unsent ?? buffer.Take(BatchSize);
            if (batch.Entries.Count == 0 && batch.Dropped == 0)
                return;

            try
            {
                await server.UploadLogsAsync(new AgentLogBatchDto { Entries = batch.Entries, Dropped = batch.Dropped }, ct);
                _unsent = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _unsent = batch;
                logger.LogDebug(ex, "Log upload failed, retrying later");
                return;
            }
            catch (OperationCanceledException)
            {
                _unsent = batch;
                return;
            }

            if (batch.Entries.Count < BatchSize)
                return;
        }
    }
}
