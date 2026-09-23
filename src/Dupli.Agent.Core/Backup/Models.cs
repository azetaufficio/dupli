namespace Dupli.Agent.Core.Backup;

/// <summary>
/// Repository location and credentials. Not a record on purpose: the generated
/// <c>ToString</c> would leak the password into logs.
/// </summary>
public sealed class RepositoryTarget(
    string repository,
    string password,
    IReadOnlyDictionary<string, string>? environment = null,
    bool readOnly = false)
{
    public string Repository { get; } = repository;
    public string Password { get; } = password;

    /// <summary>
    /// Read-only access (the server browsing an agent's repository): listings run with <c>--no-lock</c>, so they
    /// never wait for, nor leave behind, a lock the agent would have to deal with.
    /// </summary>
    public bool ReadOnly { get; } = readOnly;

    /// <summary>Backend credentials (e.g. AWS_ACCESS_KEY_ID). Values are secret.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; } =
        environment ?? new Dictionary<string, string>();

    public override string ToString() => Repository;
}

public abstract class BackupInput;

public sealed class PathsInput(IReadOnlyList<string> paths, IReadOnlyList<string> excludes) : BackupInput
{
    public IReadOnlyList<string> Paths { get; } = paths;
    public IReadOnlyList<string> Excludes { get; } = excludes;

    public override string ToString() => string.Join(";", Paths);
}

/// <summary>
/// Backs up the stdout of a command, streamed without temporary files.
/// The backup fails if the command exits with a non-zero code.
/// </summary>
public sealed class StdinCommandInput(
    string fileName,
    string executable,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string>? environment = null) : BackupInput
{
    public string FileName { get; } = fileName;
    public string Executable { get; } = executable;
    public IReadOnlyList<string> Arguments { get; } = arguments;

    /// <summary>Extra environment for the command (e.g. PGPASSWORD). Values are secret.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; } =
        environment ?? new Dictionary<string, string>();

    public override string ToString() => $"{FileName} <- {Path.GetFileName(Executable)}";
}

public sealed record BackupRequest(
    RepositoryTarget Repository,
    string Host,
    IReadOnlyList<string> Tags,
    BackupInput Input);

public sealed record BackupResult(
    string SnapshotId,
    long BytesProcessed,
    long BytesAdded,
    long FilesProcessed,
    IReadOnlyList<string> Warnings);

public sealed record SnapshotInfo(
    string Id,
    string ShortId,
    DateTimeOffset Time,
    string Host,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Tags);

/// <param name="Verify">Re-read restored files and check their content against the repository.</param>
public sealed record RestoreRequest(
    RepositoryTarget Repository,
    string SnapshotId,
    string TargetDirectory,
    IReadOnlyList<string> Includes,
    bool Verify = false);

public sealed record SnapshotFile(string Path, long Size);

public enum SnapshotNodeType
{
    File,
    Directory,
    Symlink,
    Other,
}

/// <summary>A direct child of a snapshot directory (restic paths, forward slashes).</summary>
public sealed record SnapshotNode(string Name, string Path, SnapshotNodeType Type, long Size, DateTimeOffset? ModifiedAt);

public sealed record ForgetRequest(
    RepositoryTarget Repository,
    string Host,
    IReadOnlyList<string> Tags,
    int KeepDaily,
    int KeepWeekly,
    int KeepMonthly,
    bool Prune);

public sealed record CheckResult(bool Success, IReadOnlyList<string> Messages);
