using System.Globalization;
using System.Text.Json;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Core.Restic;

public sealed class ResticBackupEngine(
    IResticBinaryProvider binary,
    IProcessRunner runner,
    ILogger<ResticBackupEngine> logger,
    TimeSpan? probeTimeout = null) : IBackupEngine
{
    private readonly TimeSpan repositoryProbeTimeout = probeTimeout ?? TimeSpan.FromMinutes(2);

    public async Task EnsureRepositoryAsync(RepositoryTarget repository, CancellationToken cancellationToken)
    {
        // `cat config` exits with 10 when the repository does not exist (restic >= 0.17).
        // A missing bucket or unreachable endpoint makes restic retry for ~15 minutes: fail fast instead.
        ProcessResult probe;
        using (var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            probeTimeout.CancelAfter(repositoryProbeTimeout);
            try
            {
                probe = await RunAsync(repository, ["cat", "config"], null, probeTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw BackupException.Transient(
                    $"Repository {repository} not reachable within {repositoryProbeTimeout.TotalSeconds:0}s " +
                    "(check endpoint, credentials and that the bucket exists)");
            }
        }
        if (probe.ExitCode == ResticExitCodes.Success)
            return;
        if (probe.ExitCode != ResticExitCodes.RepositoryMissing)
            throw ResticErrors.FromExitCode("cat config", probe.ExitCode, probe.StderrTail);

        logger.LogInformation("Initializing restic repository {Repository}", repository);
        var init = await RunAsync(repository, ["init"], null, cancellationToken);
        if (init.ExitCode != ResticExitCodes.Success)
            throw ResticErrors.FromExitCode("init", init.ExitCode, init.StderrTail);
    }

    public async Task<BackupResult> BackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string> { "backup", "--json", "--host", request.Host };
        foreach (var tag in request.Tags)
            args.AddRange(["--tag", tag]);

        IReadOnlyDictionary<string, string>? extraEnv = null;
        switch (request.Input)
        {
            case PathsInput paths:
                foreach (var exclude in paths.Excludes)
                    args.AddRange(["--exclude", exclude]);
                args.Add("--");
                args.AddRange(paths.Paths);
                break;

            case StdinCommandInput stdin:
                // restic passes its own environment to the child command.
                extraEnv = stdin.Environment;
                args.AddRange(["--stdin-from-command", "--stdin-filename", stdin.FileName, "--", stdin.Executable]);
                args.AddRange(stdin.Arguments);
                break;

            default:
                throw new ArgumentException($"Unsupported input {request.Input.GetType().Name}");
        }

        string? snapshotId = null;
        long bytesProcessed = 0, bytesAdded = 0, filesProcessed = 0;
        var warnings = new List<string>();

        var result = await RunAsync(request.Repository, args, extraEnv, cancellationToken, line =>
        {
            if (!TryParse(line, out var doc))
                return;
            using (doc)
            {
                var root = doc.RootElement;
                switch (GetString(root, "message_type"))
                {
                    case "summary":
                        snapshotId = GetString(root, "snapshot_id");
                        bytesProcessed = GetLong(root, "total_bytes_processed");
                        bytesAdded = GetLong(root, "data_added");
                        filesProcessed = GetLong(root, "total_files_processed");
                        break;
                    case "error":
                        var item = GetString(root, "item");
                        var message = root.TryGetProperty("error", out var err)
                            ? GetString(err, "message") ?? err.ToString()
                            : line;
                        warnings.Add(item is null ? message : $"{item}: {message}");
                        break;
                }
            }
        });

        // Exit 3: snapshot created but some files could not be read.
        if (result.ExitCode is not (ResticExitCodes.Success or ResticExitCodes.IncompleteSnapshot))
            throw ResticErrors.FromExitCode("backup", result.ExitCode, result.StderrTail);
        if (snapshotId is null)
            throw BackupException.Permanent("restic backup completed without reporting a snapshot id");

        return new BackupResult(snapshotId, bytesProcessed, bytesAdded, filesProcessed, warnings);
    }

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        RepositoryTarget repository,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "snapshots", "--json" };
        foreach (var tag in tags)
            args.AddRange(["--tag", tag]);

        var output = new List<string>();
        var result = await RunAsync(repository, args, null, cancellationToken, output.Add);
        if (result.ExitCode != ResticExitCodes.Success)
            throw ResticErrors.FromExitCode("snapshots", result.ExitCode, result.StderrTail);

        using var doc = JsonDocument.Parse(string.Join('\n', output));
        return doc.RootElement.EnumerateArray()
            .Select(s => new SnapshotInfo(
                Id: GetString(s, "id")!,
                ShortId: GetString(s, "short_id")!,
                Time: DateTimeOffset.Parse(GetString(s, "time")!, CultureInfo.InvariantCulture),
                Host: GetString(s, "hostname") ?? "",
                Paths: GetStrings(s, "paths"),
                Tags: GetStrings(s, "tags")))
            .ToList();
    }

    public async Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string> { "restore", request.SnapshotId, "--target", request.TargetDirectory };
        foreach (var include in request.Includes)
            args.AddRange(["--include", include]);
        if (request.Verify)
            args.Add("--verify");

        var result = await RunAsync(request.Repository, args, null, cancellationToken);
        if (result.ExitCode != ResticExitCodes.Success)
            throw ResticErrors.FromExitCode("restore", result.ExitCode, result.StderrTail);
    }

    public async Task<IReadOnlyList<SnapshotFile>> ListFilesAsync(
        RepositoryTarget repository,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        // One JSON object per line: the snapshot first, then one per node.
        var files = new List<SnapshotFile>();
        var result = await RunAsync(repository, ["ls", "--json", snapshotId], null, cancellationToken, line =>
        {
            if (!TryParse(line, out var doc))
                return;
            using (doc)
            {
                var root = doc.RootElement;
                if (GetString(root, "type") == "file" && GetString(root, "path") is { } path)
                    files.Add(new SnapshotFile(path, GetLong(root, "size")));
            }
        });
        if (result.ExitCode != ResticExitCodes.Success)
            throw ResticErrors.FromExitCode("ls", result.ExitCode, result.StderrTail);
        return files;
    }

    public async Task ForgetAsync(ForgetRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "forget", "--host", request.Host, "--group-by", "host,paths,tags",
            "--keep-daily", request.KeepDaily.ToString(CultureInfo.InvariantCulture),
            "--keep-weekly", request.KeepWeekly.ToString(CultureInfo.InvariantCulture),
            "--keep-monthly", request.KeepMonthly.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var tag in request.Tags)
            args.AddRange(["--tag", tag]);
        if (request.Prune)
            args.Add("--prune");

        var result = await RunAsync(request.Repository, args, null, cancellationToken);
        if (result.ExitCode != ResticExitCodes.Success)
            throw ResticErrors.FromExitCode("forget", result.ExitCode, result.StderrTail);
    }

    public async Task<CheckResult> CheckAsync(
        RepositoryTarget repository,
        int readDataSubsetPercent,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "check" };
        if (readDataSubsetPercent > 0)
            args.Add($"--read-data-subset={readDataSubsetPercent}%");

        var output = new List<string>();
        var result = await RunAsync(repository, args, null, cancellationToken, output.Add);
        return result.ExitCode switch
        {
            ResticExitCodes.Success => new CheckResult(true, output),
            ResticExitCodes.Fatal when !ResticErrors.LooksTransient(string.Join('\n', result.StderrTail)) =>
                new CheckResult(false, [.. output, .. result.StderrTail]),
            _ => throw ResticErrors.FromExitCode("check", result.ExitCode, result.StderrTail),
        };
    }

    public async Task UnlockStaleAsync(RepositoryTarget repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["unlock"], null, cancellationToken);
        if (result.ExitCode != ResticExitCodes.Success)
            throw ResticErrors.FromExitCode("unlock", result.ExitCode, result.StderrTail);
    }

    private async Task<ProcessResult> RunAsync(
        RepositoryTarget repository,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? extraEnv,
        CancellationToken cancellationToken,
        Action<string>? onStdout = null)
    {
        var env = new Dictionary<string, string>(repository.Environment)
        {
            ["RESTIC_REPOSITORY"] = repository.Repository,
            ["RESTIC_PASSWORD"] = repository.Password,
            // One JSON status line every 5s is enough; the summary line is what we parse.
            ["RESTIC_PROGRESS_FPS"] = "0.2",
        };
        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                env[k] = v;

        var exe = await binary.GetPathAsync(cancellationToken);
        return await runner.RunAsync(
            new ProcessSpec { FileName = exe, Arguments = args, Environment = env },
            onStdout,
            cancellationToken);
    }

    private static bool TryParse(string line, out JsonDocument doc)
    {
        doc = null!;
        if (line.Length == 0 || line[0] != '{')
            return false;
        try
        {
            doc = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static IReadOnlyList<string> GetStrings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
}
