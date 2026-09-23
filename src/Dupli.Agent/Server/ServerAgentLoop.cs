using System.Runtime.InteropServices;
using Dupli.Agent.Configuration;
using Dupli.Agent.Logging;
using Dupli.Agent.Updates;
using Dupli.Contracts.Agents;
using Dupli.Contracts.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Server;

/// <summary>
/// Server-driven mode: recover interrupted jobs, flush unacknowledged results, heartbeat, poll,
/// run at most one job at a time. Lease renewal doubles as the cancellation channel.
/// </summary>
public sealed class ServerAgentLoop(
    ServerClient server,
    JobLedger ledger,
    JobExecutor executor,
    AgentConfig config,
    AgentPaths paths,
    TimeProvider time,
    IAgentRestarter restarter,
    ILogger<ServerAgentLoop> logger,
    AgentUpdates? updates = null) : BackgroundService
{
    private HeartbeatResponse? _desired;
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(Math.Max(5, config.Server?.PollIntervalSeconds ?? 30));
    private bool _recovered;

    /// <summary>How often a running job renews its lease and checks for cancellation.</summary>
    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent {AgentId} connected to {Server}, polling every {Interval}",
            server.AgentId, config.Server?.Url, _pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                updates?.ReportHealthy();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Server unreachable etc.: already retried inside the client; try again next cycle.
                logger.LogWarning(ex, "Poll cycle failed");

                // The agent itself works, the server is not reachable: must not roll back a fresh update.
                if (IsServerUnavailable(ex))
                    updates?.ReportHealthy();
            }

            try
            {
                await Task.Delay(_pollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One cycle. Public so tests can drive the agent deterministically.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!_recovered)
        {
            RecoverInterrupted();
            _recovered = true;
        }

        await FlushReportsAsync(ct);
        await HeartbeatAsync(ct);

        foreach (var job in await server.GetJobsAsync(ct))
        {
            if (!ledger.TryBegin(job.JobId, job.Type, time.GetUtcNow()))
            {
                logger.LogInformation("Job {JobId} already handled locally, not executed again", job.JobId);
                continue;
            }
            await RunJobAsync(job, ct);
        }

        // Idle here (jobs run one at a time, inline): safe to swap restic or hand over to a new agent version.
        if (updates is not null && _desired is not null && await updates.ApplyAsync(_desired, ct))
            restarter.ExitForUpdate();
    }

    internal static bool IsServerUnavailable(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500,
        TimeoutException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };

    private void RecoverInterrupted()
    {
        foreach (var (jobId, startedAt) in ledger.Interrupted())
        {
            logger.LogWarning("Job {JobId} was running when the agent stopped: reporting it as interrupted", jobId);
            ledger.MarkFinished(jobId, new JobResultDto
            {
                Outcome = JobOutcome.Interrupted,
                StartedAt = startedAt,
                CompletedAt = time.GetUtcNow(),
                Error = "Agent restarted while the job was running",
                ErrorKind = "Transient",
            });
        }
    }

    private async Task FlushReportsAsync(CancellationToken ct)
    {
        foreach (var pending in ledger.PendingReports())
        {
            try
            {
                await server.ReportAsync(pending.JobId, pending.Result, ct);
                ledger.MarkReported(pending.JobId);
            }
            catch (ServerRejectedException ex)
            {
                // e.g. job deleted server-side: keeping it would block the outbox forever.
                logger.LogWarning("Server rejected the result of job {JobId} ({Status}); dropping it", pending.JobId, ex.Status);
                ledger.MarkReported(pending.JobId);
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var request = new HeartbeatRequest
        {
            Hostname = Environment.MachineName,
            Version = AgentVersion.Current,
            ResticVersion = config.ResticManifest.Version,
            OsVersion = RuntimeInformation.OSDescription,
            LastBackupAt = ledger.LastSuccessfulBackupAt(),
            RunningJobs = ledger.RunningJobIds(),
            FreeDiskSpace = FreeDiskSpace(),
        };
        var response = await server.HeartbeatAsync(updates?.Describe(request) ?? request, ct);
        _desired = response;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(5, response.PollIntervalSeconds));
    }

    private async Task RunJobAsync(AgentJobDto job, CancellationToken stoppingToken)
    {
        using var _ = SerilogSetup.PushRun(job.JobId);
        var startedAt = time.GetUtcNow();
        ledger.MarkRunning(job.JobId, startedAt);
        logger.LogInformation("Starting {Type} job {JobId}", job.Type, job.JobId);

        JobResultDto result;
        var control = await server.StartedAsync(job.JobId, startedAt, stoppingToken);
        if (control.CancelRequested)
        {
            result = new JobResultDto { Outcome = JobOutcome.Cancelled, StartedAt = startedAt, CompletedAt = time.GetUtcNow(), Error = "Cancelled before start" };
        }
        else
        {
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var renewCts = new CancellationTokenSource();
            var renewal = RenewLeaseAsync(job.JobId, jobCts, renewCts.Token);
            try
            {
                result = await executor.ExecuteAsync(job, jobCts.Token);
            }
            finally
            {
                await renewCts.CancelAsync();
                await renewal;
            }

            // Service stopping mid-job: leave it Running in the ledger, it is reported as interrupted at restart.
            stoppingToken.ThrowIfCancellationRequested();
        }

        ledger.MarkFinished(job.JobId, result);
        logger.LogInformation("Job {JobId} finished: {Outcome}", job.JobId, result.Outcome);
        try
        {
            await server.ReportAsync(job.JobId, result, stoppingToken);
            ledger.MarkReported(job.JobId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            logger.LogWarning(ex, "Result of job {JobId} kept in the outbox, will be re-sent", job.JobId);
        }

        // Recorded in the ledger first, so the restart job is never executed twice.
        if (job.Payload is RestartAgentJobPayload && result.Outcome == JobOutcome.Succeeded)
        {
            logger.LogWarning("Restart requested by the server (job {JobId})", job.JobId);
            restarter.Restart();
        }
    }

    private async Task RenewLeaseAsync(string jobId, CancellationTokenSource job, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LeaseRenewalInterval, time, stop);
                var control = await server.ProgressAsync(jobId, null, stop);
                if (control.CancelRequested && !job.IsCancellationRequested)
                {
                    logger.LogWarning("Cancellation requested by the server for job {JobId}", jobId);
                    await job.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lease renewal for job {JobId} failed", jobId);
            }
        }
    }

    private long? FreeDiskSpace()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(paths.Root))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
