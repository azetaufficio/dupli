using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Tools;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Agent.Updates;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Updates;

/// <summary>
/// <see cref="ResticUpdater"/> only installs (sha256 + <c>restic version</c>, both inside
/// <see cref="IToolManager.EnsureInstalledAsync"/>) and swaps the manifest: there is no repository probe.
/// </summary>
public sealed class ResticUpdaterTests : IDisposable
{
    private static readonly ToolManifestDto Old = new() { Version = "0.18.1", DownloadUrl = "https://x/old", Sha256 = new string('a', 64) };
    private static readonly ToolManifestDto New = new() { Version = "0.19.1", DownloadUrl = "https://x/new", Sha256 = new string('b', 64) };

    private readonly TempDir _dir = new();
    private readonly AgentPaths _paths;
    private readonly ActiveResticManifest _active = new(Old);
    private readonly FakeTools _tools = new();

    public ResticUpdaterTests()
    {
        _paths = new AgentPaths(_dir.Path);
        _paths.EnsureCreated();
    }

    public void Dispose() => _dir.Dispose();

    private ResticUpdater CreateUpdater() => new(_active, _tools, _paths, TimeProvider.System, NullLoggerFactory.Instance);

    [Fact]
    public async Task Activates_after_a_successful_install_and_persists_the_manifest()
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
    public async Task Transient_install_failure_keeps_the_old_binary_and_backs_off()
    {
        _tools.Error = BackupException.Transient("download failed");
        var updater = CreateUpdater();

        await updater.TryActivateAsync(New, CancellationToken.None);
        await updater.TryActivateAsync(New, CancellationToken.None);

        Assert.Equal(Old, _active.Current);
        Assert.Contains("download failed", updater.LastError);
        Assert.Null(ResticUpdater.LoadPersisted(_paths));
        Assert.Equal(1, _tools.Calls); // backed off, not retried immediately
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
}
