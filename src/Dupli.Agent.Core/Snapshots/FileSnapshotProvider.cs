using Dupli.Agent.Core.Errors;

namespace Dupli.Agent.Core.Snapshots;

/// <summary>A consistent view of paths to back up. Disposing releases the snapshot.</summary>
public abstract class FileSnapshot : IAsyncDisposable
{
    /// <summary>Paths to hand to the backup engine (may differ from the originals, e.g. VSS).</summary>
    public abstract IReadOnlyList<string> Paths { get; }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public interface IFileSnapshotProvider
{
    Task<FileSnapshot> CreateAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>No snapshot: reads live files. A future VssFileSnapshotProvider replaces this.</summary>
public sealed class DirectFileSnapshotProvider : IFileSnapshotProvider
{
    public Task<FileSnapshot> CreateAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var missing = paths.Where(p => !Directory.Exists(p) && !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw BackupException.Permanent($"Source path not found: {string.Join(", ", missing)}");

        return Task.FromResult<FileSnapshot>(new DirectSnapshot(paths));
    }

    private sealed class DirectSnapshot(IReadOnlyList<string> paths) : FileSnapshot
    {
        public override IReadOnlyList<string> Paths { get; } = paths;
    }
}
