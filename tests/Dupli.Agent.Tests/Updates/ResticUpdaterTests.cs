using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Tools;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Agent.Updates;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Updates;

public sealed class ResticUpdaterTests : IDisposable
{
    private static readonly ToolManifestDto Old = new() { Version = "0.18.1", DownloadUrl = "https://x/old", Sha256 = new string('a', 64) };
    private static readonly ToolManifestDto New = new() { Version = "0.19.1", DownloadUrl = "https://x/new", Sha256 = new string('b', 64) };

    private readonly TempDir _dir = new();
    private readonly AgentPaths _paths;
    private readonly ActiveResticManifest _active = new(Old);
    private readonly FakeTools _tools = new();
    private Exception? _probeError;
    private int _probes;

    public ResticUpdaterTests()
    {
        _paths = new AgentPaths(_dir.Path);
        _paths.EnsureCreated();
    }

    public void Dispose() => _dir.Dispose();

    private ResticUpdater CreateUpdater() => new(
        _active, _tools, _ => new ProbeEngine(this), new RepositoryTarget("s3:https://s3.test/bucket/vm", "pw"),
        _paths, TimeProvider.System, NullLoggerFactory.Instance);

    [Fact]
    public async Task Activates_after_a_successful_probe_and_persists_the_manifest()
    {
        Directory.CreateDirectory(Path.Combine(_paths.ResticTools, "0.17.0"));
        Directory.CreateDirectory(Path.Combine(_paths.ResticTools, Old.Version));
        var updater = CreateUpdater();

        await updater.TryActivateAsync(New, CancellationToken.None);

        Assert.Equal(New, _active.Current);
        Assert.Null(updater.LastError);
        Assert.Equal(New, ResticUpdater.LoadPersisted(_paths));
        Assert.True(Directory.Exists(Path.Combine(_paths.ResticTools, Old.Version)), "previous release kept");
        Assert.False(Directory.Exists(Path.Combine(_paths.ResticTools, "0.17.0")), "older releases removed");
    }

    [Fact]
    public async Task Transient_probe_failure_keeps_the_old_binary_and_backs_off()
    {
        _probeError = BackupException.Transient("S3 unreachable");
        var updater = CreateUpdater();

        await updater.TryActivateAsync(New, CancellationToken.None);
        await updater.TryActivateAsync(New, CancellationToken.None);

        Assert.Equal(Old, _active.Current);
        Assert.Contains("S3 unreachable", updater.LastError);
        Assert.Null(ResticUpdater.LoadPersisted(_paths));
        Assert.Equal(1, _probes);
    }

    [Fact]
    public async Task Permanent_failure_is_reported_and_not_retried()
    {
        _tools.Error = BackupException.Permanent("SHA256 mismatch");
        var updater = CreateUpdater();

        await updater.TryActivateAsync(New, CancellationToken.None);
        await updater.TryActivateAsync(New, CancellationToken.None);

        Assert.Equal(Old, _active.Current);
        Assert.Contains("SHA256 mismatch", updater.LastError);
        Assert.Equal(1, _tools.Calls);
    }

    [Fact]
    public async Task Desired_equal_to_active_clears_the_error()
    {
        var updater = CreateUpdater();

        await updater.TryActivateAsync(Old, CancellationToken.None);

        Assert.Null(updater.LastError);
        Assert.Equal(0, _tools.Calls);
    }

    private sealed class FakeTools : IToolManager
    {
        public Exception? Error { get; set; }
        public int Calls { get; private set; }

        public Task<string> EnsureInstalledAsync(ToolManifestDto manifest, CancellationToken cancellationToken)
        {
            Calls++;
            return Error is null ? Task.FromResult("/tools/restic/" + manifest.Version + "/restic") : Task.FromException<string>(Error);
        }
    }

    private sealed class ProbeEngine(ResticUpdaterTests test) : IBackupEngine
    {
        public Task EnsureRepositoryAsync(RepositoryTarget repository, CancellationToken cancellationToken)
        {
            test._probes++;
            return test._probeError is null ? Task.CompletedTask : Task.FromException(test._probeError);
        }

        public Task<BackupResult> BackupAsync(BackupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(RepositoryTarget repository, IReadOnlyList<string> tags, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotFile>> ListFilesAsync(RepositoryTarget repository, string snapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ForgetAsync(ForgetRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CheckResult> CheckAsync(RepositoryTarget repository, int readDataSubsetPercent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UnlockStaleAsync(RepositoryTarget repository, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
