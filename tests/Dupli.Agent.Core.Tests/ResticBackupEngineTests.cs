using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Tests.Infrastructure;

namespace Dupli.Agent.Core.Tests;

[Collection(ResticCollection.Name)]
public sealed class ResticBackupEngineTests(ResticFixture restic) : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly IBackupEngine _engine = restic.CreateEngine();

    private RepositoryTarget Repo(string password = "test-password") =>
        new(_tmp.Combine("repo"), password, new Dictionary<string, string> { ["RESTIC_CACHE_DIR"] = _tmp.Combine("cache") });

    [Fact]
    public async Task Backup_and_restore_directory_roundtrip()
    {
        var source = Directory.CreateDirectory(_tmp.Combine("data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), "world");
        await File.WriteAllTextAsync(Path.Combine(source, "skip.tmp"), "excluded");

        var repo = Repo();
        await _engine.EnsureRepositoryAsync(repo, default);
        await _engine.EnsureRepositoryAsync(repo, default); // idempotent

        var result = await _engine.BackupAsync(new BackupRequest(
            repo, "vm-test", ["policy=p1", "source=s1", "type=dir"],
            new PathsInput([source], ["*.tmp"])), default);

        Assert.False(string.IsNullOrEmpty(result.SnapshotId));
        Assert.Empty(result.Warnings);
        Assert.True(result.BytesProcessed > 0);

        var snapshots = await _engine.ListSnapshotsAsync(repo, ["policy=p1"], default);
        var snap = Assert.Single(snapshots);
        Assert.Equal(result.SnapshotId, snap.Id);
        Assert.Equal("vm-test", snap.Host);
        Assert.Contains("source=s1", snap.Tags);
        Assert.Empty(await _engine.ListSnapshotsAsync(repo, ["policy=other"], default));

        var target = _tmp.Combine("restore");
        await _engine.RestoreAsync(new RestoreRequest(repo, snap.Id, target, []), default);

        // restic restores the absolute source path below the target.
        var restoredRoot = Path.Combine(target, source.TrimStart(Path.DirectorySeparatorChar).Replace(":", ""));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(restoredRoot, "a.txt")));
        Assert.Equal("world", await File.ReadAllTextAsync(Path.Combine(restoredRoot, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(restoredRoot, "skip.tmp")));
    }

    [SkippableFact]
    public async Task Stdin_command_is_backed_up_and_failing_command_fails_backup()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses /bin/sh");
        var repo = Repo();
        await _engine.EnsureRepositoryAsync(repo, default);

        var ok = await _engine.BackupAsync(new BackupRequest(repo, "vm-test", ["type=pg", "db=demo"],
            new StdinCommandInput("demo.dump", "/bin/sh", ["-c", "printf \"$SECRET_VALUE\""],
                new Dictionary<string, string> { ["SECRET_VALUE"] = "dump-content" })), default);

        var target = _tmp.Combine("restore");
        await _engine.RestoreAsync(new RestoreRequest(repo, ok.SnapshotId, target, []), default);
        Assert.Equal("dump-content", await File.ReadAllTextAsync(Path.Combine(target, "demo.dump")));

        var ex = await Assert.ThrowsAsync<BackupException>(() => _engine.BackupAsync(new BackupRequest(
            repo, "vm-test", ["type=pg"],
            new StdinCommandInput("bad.dump", "/bin/sh", ["-c", "echo partial; exit 1"])), default));
        Assert.Equal(ErrorKind.Permanent, ex.Kind);

        // A failed command must not leave a snapshot behind.
        Assert.Single(await _engine.ListSnapshotsAsync(repo, ["type=pg"], default));
    }

    [Fact]
    public async Task Wrong_password_is_permanent()
    {
        await _engine.EnsureRepositoryAsync(Repo(), default);

        var ex = await Assert.ThrowsAsync<BackupException>(() =>
            _engine.ListSnapshotsAsync(Repo("wrong"), [], default));
        Assert.Equal(ErrorKind.Permanent, ex.Kind);
        Assert.Contains("wrong repository password", ex.Message);
    }

    [Fact]
    public async Task Forget_and_check()
    {
        var source = Directory.CreateDirectory(_tmp.Combine("data")).FullName;
        var repo = Repo();
        await _engine.EnsureRepositoryAsync(repo, default);

        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "f.txt"), $"v{i}");
            await _engine.BackupAsync(new BackupRequest(repo, "vm-test", ["policy=p1"], new PathsInput([source], [])), default);
        }
        Assert.Equal(3, (await _engine.ListSnapshotsAsync(repo, [], default)).Count);

        // Same day: keep-daily 1 retains only the latest snapshot.
        await _engine.ForgetAsync(new ForgetRequest(repo, "vm-test", ["policy=p1"], 1, 0, 0, Prune: true), default);
        Assert.Single(await _engine.ListSnapshotsAsync(repo, [], default));

        await _engine.UnlockStaleAsync(repo, default);
        var check = await _engine.CheckAsync(repo, readDataSubsetPercent: 100, default);
        Assert.True(check.Success, string.Join('\n', check.Messages));
    }

    public void Dispose() => _tmp.Dispose();
}

[Collection(ResticCollection.Name)]
public sealed class ResticRepositoryProbeTests(ResticFixture restic)
{
    [Fact]
    public async Task Unreachable_repository_fails_fast_as_transient()
    {
        var engine = new Restic.ResticBackupEngine(
            new FixedPath(restic.ResticPath), restic.Runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Restic.ResticBackupEngine>.Instance,
            probeTimeout: TimeSpan.FromSeconds(3));

        // Nothing listens on port 1: restic would retry for ~15 minutes.
        var repo = new RepositoryTarget("s3:http://127.0.0.1:1/missing/repo", "x", new Dictionary<string, string>
        {
            ["AWS_ACCESS_KEY_ID"] = "k", ["AWS_SECRET_ACCESS_KEY"] = "s",
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<BackupException>(() => engine.EnsureRepositoryAsync(repo, default));

        Assert.Equal(ErrorKind.Transient, ex.Kind);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }

    private sealed class FixedPath(string path) : Tools.IResticBinaryProvider
    {
        public Task<string> GetPathAsync(CancellationToken cancellationToken) => Task.FromResult(path);
    }
}
