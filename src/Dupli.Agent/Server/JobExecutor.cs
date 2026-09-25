using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Restore;
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

    /// <summary>
    /// <paramref name="credentials"/> is null only for <see cref="RestartAgentJobPayload"/>, the one job type
    /// that needs no repository: <see cref="ServerAgentLoop"/> does not fetch credentials for it.
    /// </summary>
    public async Task<JobResultDto> ExecuteAsync(AgentJobDto job, JobCredentials? credentials, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        try
        {
            return job.Payload switch
            {
                BackupJobPayload backup => await BackupAsync(backup, Repository(credentials), credentials!, cancellationToken),
                RetentionJobPayload retention => await RetentionAsync(retention, Repository(credentials), startedAt, cancellationToken),
                RepositoryCheckJobPayload check => await CheckAsync(check, Repository(credentials), startedAt, cancellationToken),
                RestoreTestJobPayload restoreTest => await RestoreTestAsync(job.JobId, restoreTest, Repository(credentials), startedAt, cancellationToken),
                RestoreJobPayload restore => await RestoreAsync(job.JobId, restore, Repository(credentials), credentials!, startedAt, cancellationToken),
                // The restart itself happens after the result is recorded (see ServerAgentLoop).
                RestartAgentJobPayload => new JobResultDto { Outcome = JobOutcome.Succeeded, StartedAt = startedAt, CompletedAt = time.GetUtcNow() },
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

    private async Task<JobResultDto> BackupAsync(BackupJobPayload payload, RepositoryTarget repository, ISecretStore credentials, CancellationToken ct)
    {
        ledger.SavePolicy(payload.Policy, time.GetUtcNow());
        var result = await runtime.PolicyRunner.RunAsync(payload.Policy, repository, runtime.Config.Host, credentials, ct);
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

    private async Task<JobResultDto> RetentionAsync(RetentionJobPayload payload, RepositoryTarget repository, DateTimeOffset startedAt, CancellationToken ct)
    {
        await runtime.Engine.EnsureRepositoryAsync(repository, ct);
        await runtime.Engine.UnlockStaleAsync(repository, ct);

        var items = new List<JobItemResultDto>();
        for (var i = 0; i < payload.Policies.Count; i++)
        {
            var policy = payload.Policies[i];
            // prune is repository-wide: run it once, with the last forget.
            var prune = payload.Prune && i == payload.Policies.Count - 1;
            await runtime.Engine.ForgetAsync(new ForgetRequest(
                repository, runtime.Config.Host, [BackupTags.Policy(policy.PolicyId)],
                policy.Retention.KeepDaily, policy.Retention.KeepWeekly, policy.Retention.KeepMonthly, prune), ct);
            items.Add(new JobItemResultDto { SourceId = "retention", Item = policy.PolicyId, Outcome = JobOutcome.Succeeded });
        }

        return new JobResultDto { Outcome = JobOutcome.Succeeded, StartedAt = startedAt, CompletedAt = time.GetUtcNow(), Items = items };
    }

    private async Task<JobResultDto> CheckAsync(RepositoryCheckJobPayload payload, RepositoryTarget repository, DateTimeOffset startedAt, CancellationToken ct)
    {
        await runtime.Engine.UnlockStaleAsync(repository, ct);
        var result = await runtime.Engine.CheckAsync(repository, payload.ReadDataSubsetPercent, ct);
        return result.Success
            ? new JobResultDto { Outcome = JobOutcome.Succeeded, StartedAt = startedAt, CompletedAt = time.GetUtcNow() }
            : Failed(startedAt, string.Join('\n', result.Messages), ErrorKind.Permanent);
    }

    private async Task<JobResultDto> RestoreTestAsync(string jobId, RestoreTestJobPayload payload, RepositoryTarget repository, DateTimeOffset startedAt, CancellationToken ct)
    {
        // Same rule as a manual restore: never inside a backed-up path.
        var workDirectory = RestoreGuard.ResolveTarget(
            Path.Combine(runtime.Paths.Tmp, "restore-test", jobId), jobId, runtime.Paths, payload.Policies);

        await runtime.Engine.UnlockStaleAsync(repository, ct);
        var result = await runtime.RestoreTester.RunAsync(repository, payload.Policies, payload.SampleFiles, workDirectory, ct);
        var failed = result.Items.Where(i => !i.Success).ToList();

        return new JobResultDto
        {
            Outcome = failed.Count == 0 ? JobOutcome.Succeeded : JobOutcome.Failed,
            StartedAt = startedAt,
            CompletedAt = time.GetUtcNow(),
            Error = failed.Count == 0 ? null : Truncate($"{failed.Count} of {result.Items.Count} checks failed; first: {failed[0].Item}: {failed[0].Error}"),
            ErrorKind = failed.Count == 0 ? null : nameof(ErrorKind.Permanent),
            Items = result.Items.Select(i => new JobItemResultDto
            {
                SourceId = i.SourceId,
                Item = i.Item,
                Outcome = i.Success ? JobOutcome.Succeeded : JobOutcome.Failed,
                SnapshotId = i.SnapshotId,
                BytesProcessed = i.Bytes,
                Error = i.Error,
            }).ToList(),
        };
    }

    private async Task<JobResultDto> RestoreAsync(
        string jobId, RestoreJobPayload payload, RepositoryTarget repository, ISecretStore credentials, DateTimeOffset startedAt, CancellationToken ct)
    {
        // Policies from the server plus the ones cached here: a stale server view must not open a hole in the guard.
        var policies = payload.Policies.Concat(ledger.KnownPolicies()).ToList();
        var target = RestoreGuard.ResolveTarget(payload.TargetDirectory, jobId, runtime.Paths, policies);
        RestoreGuard.EnsureEmpty(target);

        logger.LogInformation("Restoring snapshot {SnapshotId} ({Count} includes) to {Target}",
            payload.SnapshotId, payload.Includes.Count, target);
        await runtime.Engine.RestoreAsync(new RestoreRequest(repository, payload.SnapshotId, target, payload.Includes), ct);

        var items = new List<JobItemResultDto>
        {
            new() { SourceId = "restore", Item = payload.SnapshotId, Outcome = JobOutcome.Succeeded, SnapshotId = payload.SnapshotId, Location = target },
        };

        if (payload.Postgres is { } pg)
        {
            var dump = Path.Combine(target, pg.Database + ".dump");
            var password = credentials.GetRequired(pg.Source.PasswordSecret);
            var warnings = await runtime.PostgresRestorer.RestoreAsync(pg.Source, password, dump, pg.NewDatabase, ct);
            items.Add(new JobItemResultDto
            {
                SourceId = pg.Source.SourceId,
                Item = pg.Database,
                Outcome = warnings.Count == 0 ? JobOutcome.Succeeded : JobOutcome.SucceededWithWarnings,
                SnapshotId = payload.SnapshotId,
                Warnings = warnings,
                Location = $"{pg.Source.Host}:{pg.Source.Port}/{pg.NewDatabase}",
            });
        }

        return new JobResultDto
        {
            Outcome = items.Any(i => i.Outcome == JobOutcome.SucceededWithWarnings) ? JobOutcome.SucceededWithWarnings : JobOutcome.Succeeded,
            StartedAt = startedAt,
            CompletedAt = time.GetUtcNow(),
            Items = items,
        };
    }

    private RepositoryTarget Repository(JobCredentials? credentials) => runtime.Config.Repository.Resolve(
        credentials ?? throw new InvalidOperationException("This job type requires credentials"),
        runtime.Config.ResticCacheDir ?? runtime.Paths.Cache);

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
