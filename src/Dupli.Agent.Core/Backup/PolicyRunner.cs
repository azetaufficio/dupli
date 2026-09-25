using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Postgres;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Core.Snapshots;
using Dupli.Contracts.Policies;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Dupli.Agent.Core.Backup;

public enum RunStatus
{
    Succeeded,
    SucceededWithWarnings,
    Failed,
}

public sealed record ItemRunResult(
    string SourceId,
    string Item,
    RunStatus Status,
    string? SnapshotId,
    long BytesProcessed,
    long BytesAdded,
    IReadOnlyList<string> Warnings,
    string? Error,
    ErrorKind? ErrorKind);

public sealed record PolicyRunResult(
    string PolicyId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ItemRunResult> Items)
{
    public RunStatus Status =>
        Items.Count == 0 || Items.Any(i => i.Status == RunStatus.Failed) ? RunStatus.Failed
        : Items.Any(i => i.Status == RunStatus.SucceededWithWarnings) ? RunStatus.SucceededWithWarnings
        : RunStatus.Succeeded;
}

public sealed record RetryOptions(int MaxRetries = 3, TimeSpan? BaseDelay = null)
{
    public TimeSpan Delay => BaseDelay ?? TimeSpan.FromSeconds(30);
}

public static class BackupTags
{
    public static string Policy(string id) => $"policy={Sanitize(id)}";
    public static string Source(string id) => $"source={Sanitize(id)}";
    public static string Type(string type) => $"type={type}";
    public static string Database(string name) => $"db={Sanitize(name)}";

    // restic splits --tag values on commas.
    private static string Sanitize(string value) =>
        value.Contains(',') ? throw new ArgumentException($"Tag value must not contain ',': {value}") : value;
}

/// <summary>
/// Executes a backup policy: every source item is backed up independently, so one failing
/// database does not prevent the others. Transient failures are retried with exponential backoff.
/// </summary>
public sealed class PolicyRunner(
    IBackupEngine engine,
    IDatabaseBackupProvider databases,
    IFileSnapshotProvider snapshots,
    RetryOptions retry,
    TimeProvider time,
    ILogger<PolicyRunner> logger)
{
    private readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<BackupException>(e => e.Kind == ErrorKind.Transient),
            MaxRetryAttempts = retry.MaxRetries,
            Delay = retry.Delay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            OnRetry = args =>
            {
                logger.LogWarning(args.Outcome.Exception,
                    "Transient failure, retry {Attempt} in {Delay}", args.AttemptNumber + 1, args.RetryDelay);
                return ValueTask.CompletedTask;
            },
        })
        .Build();

    public async Task<PolicyRunResult> RunAsync(
        PolicySpecDto policy,
        RepositoryTarget repository,
        string host,
        ISecretStore secrets,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["PolicyId"] = policy.PolicyId });
        var startedAt = time.GetUtcNow();
        var items = new List<ItemRunResult>();

        try
        {
            await _pipeline.ExecuteAsync(async ct =>
            {
                await engine.EnsureRepositoryAsync(repository, ct);
                await engine.UnlockStaleAsync(repository, ct);
            }, cancellationToken);
        }
        catch (BackupException ex)
        {
            logger.LogError(ex, "Repository {Repository} not usable", repository);
            items.Add(Failed("repository", repository.Repository, ex));
            return new PolicyRunResult(policy.PolicyId, startedAt, time.GetUtcNow(), items);
        }

        foreach (var source in policy.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseTags = new[] { BackupTags.Policy(policy.PolicyId), BackupTags.Source(source.SourceId) };

            switch (source)
            {
                case DirectorySourceDto dir:
                    items.Add(await BackupItemAsync(source.SourceId, string.Join(";", dir.Paths), async ct =>
                    {
                        await using var snapshot = await snapshots.CreateAsync(dir.Paths, ct);
                        return await engine.BackupAsync(new BackupRequest(
                            repository, host, [.. baseTags, BackupTags.Type("dir")],
                            new PathsInput(snapshot.Paths, dir.Excludes)), ct);
                    }, cancellationToken));
                    break;

                case PostgresSourceDto pg:
                    items.AddRange(await BackupPostgresAsync(pg, repository, host, baseTags, secrets, cancellationToken));
                    break;
            }
        }

        var result = new PolicyRunResult(policy.PolicyId, startedAt, time.GetUtcNow(), items);
        logger.LogInformation("Policy {PolicyName} finished: {Status} ({Count} items)",
            policy.Name, result.Status, items.Count);
        return result;
    }

    private async Task<IReadOnlyList<ItemRunResult>> BackupPostgresAsync(
        PostgresSourceDto pg,
        RepositoryTarget repository,
        string host,
        string[] baseTags,
        ISecretStore secrets,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DatabaseDump> dumps;
        try
        {
            var password = secrets.GetRequired(pg.PasswordSecret);
            dumps = await _pipeline.ExecuteAsync(
                async ct => await databases.PlanDumpsAsync(pg, password, ct), cancellationToken);
        }
        catch (Exception ex) when (ex is BackupException or InvalidOperationException)
        {
            logger.LogError(ex, "PostgreSQL source {SourceId} cannot be backed up", pg.SourceId);
            return [Failed(pg.SourceId, $"{pg.Host}:{pg.Port}", ex)];
        }

        var results = new List<ItemRunResult>();
        foreach (var dump in dumps)
        {
            results.Add(await BackupItemAsync(pg.SourceId, dump.Database, ct =>
                engine.BackupAsync(new BackupRequest(
                    repository, host,
                    [.. baseTags, BackupTags.Type("pg"), BackupTags.Database(dump.Database)],
                    dump.Input), ct), cancellationToken));
        }
        return results;
    }

    private async Task<ItemRunResult> BackupItemAsync(
        string sourceId,
        string item,
        Func<CancellationToken, Task<BackupResult>> backup,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["SourceId"] = sourceId, ["Item"] = item });
        try
        {
            var r = await _pipeline.ExecuteAsync(async ct => await backup(ct), cancellationToken);
            foreach (var w in r.Warnings)
                logger.LogWarning("Backup warning: {Warning}", w);
            logger.LogInformation("Snapshot {SnapshotId} created ({Bytes} bytes processed, {Added} added)",
                r.SnapshotId, r.BytesProcessed, r.BytesAdded);

            return new ItemRunResult(sourceId, item,
                r.Warnings.Count > 0 ? RunStatus.SucceededWithWarnings : RunStatus.Succeeded,
                r.SnapshotId, r.BytesProcessed, r.BytesAdded, r.Warnings, null, null);
        }
        catch (BackupException ex)
        {
            logger.LogError(ex, "Backup of {Item} failed ({Kind})", item, ex.Kind);
            return Failed(sourceId, item, ex);
        }
    }

    private static ItemRunResult Failed(string sourceId, string item, Exception ex) =>
        new(sourceId, item, RunStatus.Failed, null, 0, 0, [], ex.Message,
            (ex as BackupException)?.Kind ?? ErrorKind.Permanent);
}
