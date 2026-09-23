using Dupli.Agent.Cli;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Contracts.Jobs;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Server;

/// <summary>
/// Maps a typed job to agent operations. There is no generic "command" job: an unknown type fails
/// permanently instead of being interpreted.
/// </summary>
public sealed class JobExecutor(AgentRuntime runtime, JobLedger ledger, TimeProvider time, ILogger<JobExecutor> logger)
{
    private const int MaxErrorLength = 4000;

    public async Task<JobResultDto> ExecuteAsync(AgentJobDto job, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        try
        {
            return job.Payload switch
            {
                BackupJobPayload backup => await BackupAsync(backup, cancellationToken),
                RetentionJobPayload retention => await RetentionAsync(retention, startedAt, cancellationToken),
                RepositoryCheckJobPayload check => await CheckAsync(check, startedAt, cancellationToken),
                _ => Failed(startedAt, $"Job type {job.Type} is not supported by this agent version", ErrorKind.Permanent),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} cancelled", job.JobId);
            return new JobResultDto { Outcome = JobOutcome.Cancelled, StartedAt = startedAt, CompletedAt = time.GetUtcNow(), Error = "Cancelled" };
        }
        catch (BackupException ex)
        {
            logger.LogError(ex, "Job {JobId} failed ({Kind})", job.JobId, ex.Kind);
            return Failed(startedAt, ex.Message, ex.Kind);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Job {JobId} failed", job.JobId);
            return Failed(startedAt, ex.Message, ErrorKind.Permanent);
        }
    }

    private async Task<JobResultDto> BackupAsync(BackupJobPayload payload, CancellationToken ct)
    {
        ledger.SavePolicy(payload.Policy, time.GetUtcNow());
        var result = await runtime.PolicyRunner.RunAsync(payload.Policy, runtime.Repository, runtime.Config.Host, ct);
        var failed = result.Items.FirstOrDefault(i => i.Status == RunStatus.Failed);

        return new JobResultDto
        {
            Outcome = ToOutcome(result.Status),
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            Error = failed is null ? null : Truncate($"{failed.Item}: {failed.Error}"),
            ErrorKind = failed?.ErrorKind?.ToString(),
            Items = result.Items.Select(i => new JobItemResultDto
            {
                SourceId = i.SourceId,
                Item = i.Item,
                Outcome = ToOutcome(i.Status),
                SnapshotId = i.SnapshotId,
                BytesProcessed = i.BytesProcessed,
                BytesAdded = i.BytesAdded,
                Warnings = i.Warnings,
                Error = i.Error,
            }).ToList(),
        };
    }

    private async Task<JobResultDto> RetentionAsync(RetentionJobPayload payload, DateTimeOffset startedAt, CancellationToken ct)
    {
        await runtime.Engine.EnsureRepositoryAsync(runtime.Repository, ct);
        await runtime.Engine.UnlockStaleAsync(runtime.Repository, ct);

        var items = new List<JobItemResultDto>();
        for (var i = 0; i < payload.Policies.Count; i++)
        {
            var policy = payload.Policies[i];
            // prune is repository-wide: run it once, with the last forget.
            var prune = payload.Prune && i == payload.Policies.Count - 1;
            await runtime.Engine.ForgetAsync(new ForgetRequest(
                runtime.Repository, runtime.Config.Host, [BackupTags.Policy(policy.PolicyId)],
                policy.Retention.KeepDaily, policy.Retention.KeepWeekly, policy.Retention.KeepMonthly, prune), ct);
            items.Add(new JobItemResultDto { SourceId = "retention", Item = policy.PolicyId, Outcome = JobOutcome.Succeeded });
        }

        return new JobResultDto { Outcome = JobOutcome.Succeeded, StartedAt = startedAt, CompletedAt = time.GetUtcNow(), Items = items };
    }

    private async Task<JobResultDto> CheckAsync(RepositoryCheckJobPayload payload, DateTimeOffset startedAt, CancellationToken ct)
    {
        await runtime.Engine.UnlockStaleAsync(runtime.Repository, ct);
        var result = await runtime.Engine.CheckAsync(runtime.Repository, payload.ReadDataSubsetPercent, ct);
        return result.Success
            ? new JobResultDto { Outcome = JobOutcome.Succeeded, StartedAt = startedAt, CompletedAt = time.GetUtcNow() }
            : Failed(startedAt, string.Join('\n', result.Messages), ErrorKind.Permanent);
    }

    private JobResultDto Failed(DateTimeOffset startedAt, string error, ErrorKind kind) => new()
    {
        Outcome = JobOutcome.Failed,
        StartedAt = startedAt,
        CompletedAt = time.GetUtcNow(),
        Error = Truncate(error),
        ErrorKind = kind.ToString(),
    };

    private static JobOutcome ToOutcome(RunStatus status) => status switch
    {
        RunStatus.Succeeded => JobOutcome.Succeeded,
        RunStatus.SucceededWithWarnings => JobOutcome.SucceededWithWarnings,
        _ => JobOutcome.Failed,
    };

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength] + "…";
}
