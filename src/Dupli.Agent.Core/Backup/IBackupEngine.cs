namespace Dupli.Agent.Core.Backup;

public interface IBackupEngine
{
    /// <summary>Creates the repository when it does not exist yet.</summary>
    Task EnsureRepositoryAsync(RepositoryTarget repository, CancellationToken cancellationToken);

    Task<BackupResult> BackupAsync(BackupRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        RepositoryTarget repository,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken);

    Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken);

    Task ForgetAsync(ForgetRequest request, CancellationToken cancellationToken);

    Task<CheckResult> CheckAsync(
        RepositoryTarget repository,
        int readDataSubsetPercent,
        CancellationToken cancellationToken);

    /// <summary>Removes stale locks only (locks of dead processes).</summary>
    Task UnlockStaleAsync(RepositoryTarget repository, CancellationToken cancellationToken);
}
