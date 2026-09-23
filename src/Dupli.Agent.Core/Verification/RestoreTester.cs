using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Postgres;
using Dupli.Agent.Core.Processes;
using Dupli.Contracts.Policies;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Core.Verification;

/// <param name="Item">Restored file path (restic path) or database name.</param>
/// <param name="Bytes">Size of what was restored and verified.</param>
public sealed record RestoreTestItem(string SourceId, string Item, bool Success, string? SnapshotId, long Bytes, string? Error);

public sealed record RestoreTestResult(IReadOnlyList<RestoreTestItem> Items)
{
    public bool Success => Items.All(i => i.Success);
}

/// <summary>
/// Proves that backups can be read back, without touching production data: everything is restored under
/// <c>workDirectory</c>, which is deleted afterwards.
/// <list type="bullet">
/// <item>Directory source: a random sample of files from the latest snapshot is restored with
/// <c>restic restore --verify</c> (content checked against the repository) and each restored file must exist
/// with the size recorded in the snapshot.</item>
/// <item>PostgreSQL source: the latest dump of every database must be readable by <c>pg_restore --list</c>;
/// the globals script must be non-empty.</item>
/// </list>
/// A source without any snapshot fails: there is nothing that could be restored.
/// </summary>
public sealed class RestoreTester(
    IBackupEngine engine,
    IPostgresBinLocator binLocator,
    IProcessRunner runner,
    ILogger<RestoreTester> logger,
    Random? random = null)
{
    /// <summary>Files larger than this are not sampled (the test must stay cheap on disk and bandwidth).</summary>
    public long MaxSampleFileBytes { get; init; } = 256L * 1024 * 1024;

    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
    private readonly Random _random = random ?? Random.Shared;

    public async Task<RestoreTestResult> RunAsync(
        RepositoryTarget repository,
        IReadOnlyList<PolicySpecDto> policies,
        int sampleFiles,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var items = new List<RestoreTestItem>();
        Directory.CreateDirectory(workDirectory);
        try
        {
            var step = 0;
            foreach (var policy in policies)
            {
                var snapshots = await engine.ListSnapshotsAsync(repository, [BackupTags.Policy(policy.PolicyId)], cancellationToken);
                foreach (var source in policy.Sources)
                {
                    var own = snapshots.Where(s => s.Tags.Contains(BackupTags.Source(source.SourceId))).ToList();
                    var target = Path.Combine(workDirectory, (step++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    items.AddRange(source switch
                    {
                        DirectorySourceDto => await TestDirectoryAsync(repository, source.SourceId, own, sampleFiles, target, cancellationToken),
                        PostgresSourceDto pg => await TestPostgresAsync(repository, pg, own, target, cancellationToken),
                        _ => [new RestoreTestItem(source.SourceId, source.SourceId, false, null, 0, $"Unsupported source type {source.GetType().Name}")],
                    });
                }
            }
        }
        finally
        {
            Cleanup(workDirectory);
        }

        logger.LogInformation("Restore test: {Passed}/{Total} checks passed", items.Count(i => i.Success), items.Count);
        return new RestoreTestResult(items);
    }

    private async Task<IReadOnlyList<RestoreTestItem>> TestDirectoryAsync(
        RepositoryTarget repository, string sourceId, IReadOnlyList<SnapshotInfo> snapshots, int sampleFiles, string target, CancellationToken ct)
    {
        var latest = snapshots.Where(s => s.Tags.Contains(BackupTags.Type("dir"))).MaxBy(s => s.Time);
        if (latest is null)
            return [NoSnapshot(sourceId)];

        var files = await engine.ListFilesAsync(repository, latest.Id, ct);
        // restic --include takes glob patterns: skip names that would be interpreted as one.
        var candidates = files.Where(f => f.Size <= MaxSampleFileBytes && f.Path.IndexOfAny(['*', '?', '[', ']', '\\']) < 0).ToArray();
        if (candidates.Length == 0)
            return [new RestoreTestItem(sourceId, sourceId, files.Count == 0, latest.Id, 0,
                files.Count == 0 ? null : "No file small enough to sample")];

        _random.Shuffle(candidates);
        var sample = candidates.Take(Math.Max(1, sampleFiles)).ToList();

        try
        {
            await engine.RestoreAsync(new RestoreRequest(repository, latest.Id, target, sample.Select(f => f.Path).ToList(), Verify: true), ct);
        }
        catch (BackupException ex)
        {
            return sample.Select(f => new RestoreTestItem(sourceId, f.Path, false, latest.Id, 0, $"Restore failed: {ex.Message}")).ToList();
        }

        return sample.Select(f =>
        {
            var restored = new FileInfo(RestoredPath(target, f.Path));
            if (!restored.Exists)
                return new RestoreTestItem(sourceId, f.Path, false, latest.Id, 0, "File missing after restore");
            return restored.Length == f.Size
                ? new RestoreTestItem(sourceId, f.Path, true, latest.Id, f.Size, null)
                : new RestoreTestItem(sourceId, f.Path, false, latest.Id, restored.Length,
                    $"Size mismatch: restored {restored.Length} bytes, snapshot says {f.Size}");
        }).ToList();
    }

    private async Task<IReadOnlyList<RestoreTestItem>> TestPostgresAsync(
        RepositoryTarget repository, PostgresSourceDto source, IReadOnlyList<SnapshotInfo> snapshots, string target, CancellationToken ct)
    {
        const string dbTagPrefix = "db=";
        var latestPerDatabase = snapshots
            .Where(s => s.Tags.Contains(BackupTags.Type("pg")))
            .Select(s => (Snapshot: s, Database: s.Tags.FirstOrDefault(t => t.StartsWith(dbTagPrefix, StringComparison.Ordinal))?[dbTagPrefix.Length..]))
            .Where(x => x.Database is not null)
            .GroupBy(x => x.Database!)
            .Select(g => g.MaxBy(x => x.Snapshot.Time))
            .OrderBy(x => x.Database, StringComparer.Ordinal)
            .ToList();
        if (latestPerDatabase.Count == 0)
            return [NoSnapshot(source.SourceId)];

        string? pgRestore = null;
        var items = new List<RestoreTestItem>();
        foreach (var (snapshot, database) in latestPerDatabase)
        {
            var dbTarget = Path.Combine(target, items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                await engine.RestoreAsync(new RestoreRequest(repository, snapshot.Id, dbTarget, [], Verify: true), ct);
            }
            catch (BackupException ex)
            {
                items.Add(new RestoreTestItem(source.SourceId, database!, false, snapshot.Id, 0, $"Restore failed: {ex.Message}"));
                continue;
            }

            // A stdin snapshot holds exactly one file (<db>.dump or globals.sql).
            var file = Directory.EnumerateFiles(dbTarget, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                items.Add(new RestoreTestItem(source.SourceId, database!, false, snapshot.Id, 0, "Restored dump is missing or empty"));
                continue;
            }

            if (database == PostgresDumpProvider.GlobalsName)
            {
                items.Add(new RestoreTestItem(source.SourceId, database, true, snapshot.Id, file.Length, null));
                continue;
            }

            pgRestore ??= FindPgRestore(source);
            if (pgRestore is null)
            {
                items.Add(new RestoreTestItem(source.SourceId, database!, false, snapshot.Id, file.Length,
                    "pg_restore not found; set BinDirectory on the source"));
                continue;
            }

            // Exit 0 means a readable archive with a valid table of contents (an empty database lists no entries).
            var result = await runner.RunAsync(
                new ProcessSpec { FileName = pgRestore, Arguments = ["--list", file.FullName] }, null, ct);
            items.Add(result.ExitCode == 0
                ? new RestoreTestItem(source.SourceId, database!, true, snapshot.Id, file.Length, null)
                : new RestoreTestItem(source.SourceId, database!, false, snapshot.Id, file.Length,
                    $"pg_restore --list failed (exit {result.ExitCode}): {string.Join(' ', result.StderrTail)}".Trim()));
        }
        return items;
    }

    private string? FindPgRestore(PostgresSourceDto source)
    {
        var dir = source.BinDirectory ?? binLocator.FindBinDirectory(IPostgresBinLocator.Newest);
        var exe = dir is null ? null : Path.Combine(dir, "pg_restore" + ExeSuffix);
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    /// <summary>restic recreates the snapshot path under the target (<c>/C/Data/x</c> → <c>{target}\C\Data\x</c>).</summary>
    internal static string RestoredPath(string target, string snapshotPath) =>
        Path.Combine(target, snapshotPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    private static RestoreTestItem NoSnapshot(string sourceId) =>
        new(sourceId, sourceId, false, null, 0, "No snapshot found for this source");

    private void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete restore test directory {Directory}", directory);
        }
    }
}
