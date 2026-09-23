using System.Collections.Concurrent;
using Dupli.Agent.Configuration;
using Dupli.Agent.Launcher;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Contracts.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Launcher;

public sealed class LauncherSupervisorTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly TempDir _dir = new();
    private readonly AgentPaths _paths;
    private readonly VersionFiles _files;
    private readonly FakeProcesses _processes = new();

    public LauncherSupervisorTests()
    {
        _paths = new AgentPaths(_dir.Path);
        _paths.EnsureCreated();
        _files = new VersionFiles(_paths);
    }

    public void Dispose() => _dir.Dispose();

    private LauncherSupervisor CreateSupervisor(TimeSpan? probation = null) => new(
        _files, _paths, _processes, TimeProvider.System,
        new LauncherOptions
        {
            Probation = probation ?? TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromMilliseconds(300),
            CrashBackoff = [TimeSpan.FromMilliseconds(10)],
            HealthPollInterval = TimeSpan.FromMilliseconds(20),
        },
        NullLogger<LauncherSupervisor>.Instance);

    private InstalledVersion Stage(string version, string content = "")
    {
        var directory = _files.VersionDirectory(version);
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, VersionFiles.ExeFileName);
        File.WriteAllText(exe, "agent " + version + content);
        return new InstalledVersion { Version = version, ExePath = exe, Sha256 = VersionFiles.Sha256Of(exe) };
    }

    private void ReportHealthy(FakeChild child, string version) =>
        _files.WriteHealth(new HealthFile { Version = version, Pid = child.Id, At = DateTimeOffset.UtcNow });

    [Fact]
    public async Task Update_switches_to_pending_and_confirms_after_health()
    {
        var v1 = Stage("1.0.0");
        var v0 = Stage("0.9.0");
        _files.WriteCurrent(CurrentVersionFile.From(v1));
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor().RunAsync(stop.Token);

        var first = await _processes.NextAsync();
        Assert.Equal(v1.ExePath, first.ExePath);
        Assert.Equal("1", first.Environment[LauncherSupervisor.LaunchedVariable]);
        var v2 = Stage("2.0.0");
        _files.WritePending(v2);
        first.Exit(LauncherSupervisor.UpdateExitCode);

        var second = await _processes.NextAsync();
        Assert.Equal(v2.ExePath, second.ExePath);
        Assert.True(_files.ReadCurrent()!.Probation);
        Assert.False(File.Exists(_files.PendingPath));
        ReportHealthy(second, "2.0.0");

        await WaitUntilAsync(() => _files.ReadCurrent() is { Probation: false });
        var current = _files.ReadCurrent()!;
        Assert.Equal("2.0.0", current.Version);
        Assert.Equal("1.0.0", current.Previous?.Version);
        Assert.Equal(UpdateOutcome.Succeeded, _files.ReadUpdateState().Last?.Outcome);
        Assert.False(Directory.Exists(Path.GetDirectoryName(v0.ExePath)), "versions older than previous are removed");
        Assert.True(File.Exists(v1.ExePath), "previous version is kept for rollback");

        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);
        Assert.True(second.StopRequested);
    }

    [Fact]
    public async Task Crash_during_probation_rolls_back_and_marks_the_version_failed()
    {
        var v1 = Stage("1.0.0");
        var v2 = Stage("2.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v2, previous: v1, probation: true));
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor().RunAsync(stop.Token);

        (await _processes.NextAsync()).Exit(1);
        var restarted = await _processes.NextAsync();
        Assert.Equal(v1.ExePath, restarted.ExePath);

        var current = _files.ReadCurrent()!;
        Assert.Equal("1.0.0", current.Version);
        Assert.False(current.Probation);
        var state = _files.ReadUpdateState();
        Assert.Equal(UpdateOutcome.RolledBack, state.Last?.Outcome);
        Assert.Equal("2.0.0", state.Last?.Version);
        Assert.Contains("2.0.0", state.FailedVersions);

        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task No_health_within_probation_rolls_back()
    {
        var v1 = Stage("1.0.0");
        var v2 = Stage("2.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v2, previous: v1, probation: true));
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor(probation: TimeSpan.FromMilliseconds(300)).RunAsync(stop.Token);

        var unhealthy = await _processes.NextAsync();
        var restarted = await _processes.NextAsync();
        Assert.True(unhealthy.StopRequested);
        Assert.Equal(v1.ExePath, restarted.ExePath);
        Assert.Contains("no health report", _files.ReadUpdateState().Last?.Error);

        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Health_of_another_process_does_not_end_probation()
    {
        var v1 = Stage("1.0.0");
        var v2 = Stage("2.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v2, previous: v1, probation: true));
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor(probation: TimeSpan.FromMilliseconds(400)).RunAsync(stop.Token);

        var child = await _processes.NextAsync();
        _files.WriteHealth(new HealthFile { Version = "2.0.0", Pid = child.Id + 1, At = DateTimeOffset.UtcNow });
        var restarted = await _processes.NextAsync();
        Assert.Equal(v1.ExePath, restarted.ExePath);

        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Restart_exit_code_restarts_the_same_version_and_crashes_restart_with_backoff()
    {
        var v1 = Stage("1.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v1));
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor().RunAsync(stop.Token);

        (await _processes.NextAsync()).Exit(LauncherSupervisor.RestartExitCode);
        (await _processes.NextAsync()).Exit(3);
        var third = await _processes.NextAsync();
        Assert.Equal(v1.ExePath, third.ExePath);
        Assert.Null(_files.ReadUpdateState().Last);

        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Pending_version_with_wrong_sha_is_rejected()
    {
        var v1 = Stage("1.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v1));
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor().RunAsync(stop.Token);

        var first = await _processes.NextAsync();
        _files.WritePending(Stage("2.0.0") with { Sha256 = new string('0', 64) });
        first.Exit(LauncherSupervisor.UpdateExitCode);

        var second = await _processes.NextAsync();
        Assert.Equal(v1.ExePath, second.ExePath);
        Assert.Contains("2.0.0", _files.ReadUpdateState().FailedVersions);

        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Tampered_current_executable_is_not_started()
    {
        var v1 = Stage("1.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v1));
        File.AppendAllText(v1.ExePath, "tampered");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSupervisor().RunAsync(CancellationToken.None));

        Assert.Contains("sha256", error.Message);
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task Child_ignoring_stop_is_killed()
    {
        var v1 = Stage("1.0.0");
        _files.WriteCurrent(CurrentVersionFile.From(v1));
        _processes.IgnoreStop = true;
        using var stop = new CancellationTokenSource();

        var run = CreateSupervisor().RunAsync(stop.Token);
        var child = await _processes.NextAsync();
        await stop.CancelAsync();
        await run.WaitAsync(TestTimeout);

        Assert.True(child.StopRequested);
        Assert.True(child.Killed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met");
            await Task.Delay(20);
        }
    }

    private sealed class FakeProcesses : IChildProcessFactory
    {
        private readonly BlockingCollection<FakeChild> _queue = new();
        private int _nextPid = 1000;

        public ConcurrentQueue<FakeChild> Started { get; } = new();
        public bool IgnoreStop { get; set; }

        public IChildProcess Start(string exePath, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
        {
            var child = new FakeChild(Interlocked.Increment(ref _nextPid), exePath, environment, IgnoreStop);
            Started.Enqueue(child);
            _queue.Add(child);
            return child;
        }

        public Task<FakeChild> NextAsync() => Task.Run(() =>
            _queue.TryTake(out var child, TestTimeout) ? child : throw new TimeoutException("No child started"));
    }

    private sealed class FakeChild(int id, string exePath, IReadOnlyDictionary<string, string> environment, bool ignoreStop) : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = id;
        public string ExePath { get; } = exePath;
        public IReadOnlyDictionary<string, string> Environment { get; } = environment;
        public bool StopRequested { get; private set; }
        public bool Killed { get; private set; }
        public Task<int> Exited => _exit.Task;

        public void Exit(int code) => _exit.TrySetResult(code);

        public void RequestStop()
        {
            StopRequested = true;
            if (!ignoreStop)
                Exit(0);
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public void Dispose()
        {
        }
    }
}
