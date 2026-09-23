using Dupli.Agent.Configuration;
using Dupli.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Launcher;

public sealed record LauncherOptions
{
    /// <summary>How long a freshly switched version has to report healthy before it is rolled back.</summary>
    public TimeSpan Probation { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Grace period between closing the child's stdin and killing it.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delays before restarting a crashed child; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> CrashBackoff { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>A child that ran at least this long resets the crash backoff.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Launcher core: starts <c>current.json</c>'s version as a child, restarts it when it exits, switches to
/// <c>pending.json</c> on the update exit code and rolls back to the previous version when the new one crashes
/// or does not report healthy within the probation window. The service itself never stops during an update.
/// </summary>
public sealed class LauncherSupervisor(
    VersionFiles files,
    AgentPaths paths,
    IChildProcessFactory processes,
    TimeProvider time,
    LauncherOptions options,
    ILogger<LauncherSupervisor> logger)
{
    /// <summary>Environment variable set on the child: it may apply updates and must stop when stdin closes.</summary>
    public const string LaunchedVariable = "DUPLI_LAUNCHED";

    /// <summary>Child exit code: restart (a "restart agent" job).</summary>
    public const int RestartExitCode = 75;

    /// <summary>Child exit code: a verified version is staged in <c>pending.json</c>, switch to it.</summary>
    public const int UpdateExitCode = 76;

    private int _crashes;

    public static bool IsLaunched =>
        Environment.GetEnvironmentVariable(LaunchedVariable) == "1";

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = files.ReadCurrent()
                ?? throw new InvalidOperationException($"{files.CurrentPath} not found: run 'dupli-agent install' first");

            if (!IsIntact(current.ToInstalled(), out var problem))
            {
                if (current.Probation && current.Previous is not null)
                {
                    RollBack(current, problem);
                    continue;
                }
                throw new InvalidOperationException($"Cannot start agent {current.Version}: {problem}");
            }

            var result = await RunChildAsync(current, stoppingToken);
            switch (result.Exit)
            {
                case ChildExit.Stopped:
                    return;
                case ChildExit.Update:
                    SwitchToPending(current);
                    break;
                case ChildExit.Restart:
                    break;
                case ChildExit.Unhealthy:
                    RollBack(current, $"no health report within {options.Probation.TotalMinutes:0.#} minutes");
                    break;
                case ChildExit.Crashed:
                    if (files.ReadCurrent() is { Probation: true, Previous: not null } probation)
                    {
                        RollBack(probation, $"exited with code {result.Code} during probation");
                        break;
                    }
                    await DelayAfterCrashAsync(result.Code, result.RanFor, stoppingToken);
                    break;
            }
        }
    }

    private enum ChildExit { Stopped, Update, Restart, Unhealthy, Crashed }

    private readonly record struct ChildResult(ChildExit Exit, int Code = 0, TimeSpan RanFor = default);

    private async Task<ChildResult> RunChildAsync(CurrentVersionFile current, CancellationToken stoppingToken)
    {
        files.DeleteHealth();
        var startedAt = time.GetUtcNow();
        var deadline = startedAt + options.Probation;

        using var child = processes.Start(current.ExePath, ["run"], new Dictionary<string, string>
        {
            [LaunchedVariable] = "1",
            ["DUPLI_HOME"] = paths.Root,
        });
        logger.LogInformation("Started agent {Version} (pid {Pid}){Probation}", current.Version, child.Id,
            current.Probation ? " on probation" : "");

        var probation = current.Probation;
        while (true)
        {
            var wait = probation ? options.HealthPollInterval : Timeout.InfiniteTimeSpan;
            var finished = await WaitAsync(child.Exited, wait, stoppingToken);

            if (stoppingToken.IsCancellationRequested)
            {
                await StopAsync(child);
                return new ChildResult(ChildExit.Stopped);
            }

            if (finished)
            {
                var code = await child.Exited;
                var ranFor = time.GetUtcNow() - startedAt;
                logger.Log(code is RestartExitCode or UpdateExitCode ? LogLevel.Information : LogLevel.Warning,
                    "Agent {Version} exited with code {Code} after {RanFor}", current.Version, code, ranFor);
                return code switch
                {
                    UpdateExitCode => new ChildResult(ChildExit.Update),
                    RestartExitCode => new ChildResult(ChildExit.Restart),
                    _ => new ChildResult(ChildExit.Crashed, code, ranFor),
                };
            }

            if (!probation)
                continue;

            if (files.ReadHealth() is { } health && health.Pid == child.Id && health.Version == current.Version)
            {
                ConfirmUpdate(current);
                probation = false;
            }
            else if (time.GetUtcNow() >= deadline)
            {
                logger.LogError("Agent {Version} did not report healthy within {Probation}", current.Version, options.Probation);
                await StopAsync(child);
                return new ChildResult(ChildExit.Unhealthy);
            }
        }
    }

    /// <summary>True when the child exited; false on timeout or stop.</summary>
    private async Task<bool> WaitAsync(Task exited, TimeSpan timeout, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(timeout, time, cts.Token);
        var first = await Task.WhenAny(exited, delay);
        await cts.CancelAsync();
        return first == exited;
    }

    private async Task StopAsync(IChildProcess child)
    {
        child.RequestStop();
        if (!await WaitAsync(child.Exited, options.StopTimeout, CancellationToken.None))
        {
            logger.LogWarning("Agent did not stop within {Timeout}: killing it", options.StopTimeout);
            child.Kill();
            await child.Exited;
        }
    }

    private void SwitchToPending(CurrentVersionFile current)
    {
        var pending = files.ReadPending();
        files.DeletePending();
        if (pending is null)
        {
            logger.LogWarning("Agent asked for an update but {Path} is missing: restarting the current version", files.PendingPath);
            return;
        }
        var problem = "invalid version";
        if (!AgentVersion.IsValid(pending.Version) || !IsIntact(pending, out problem))
        {
            logger.LogError("Pending agent {Version} rejected: {Problem}", pending.Version, problem);
            RecordOutcome(pending.Version, UpdateOutcome.RolledBack, "staged package failed verification");
            return;
        }

        files.WriteCurrent(CurrentVersionFile.From(pending, previous: current.ToInstalled(), probation: true));
        _crashes = 0;
        logger.LogInformation("Switched from agent {From} to {To}", current.Version, pending.Version);
    }

    private void ConfirmUpdate(CurrentVersionFile current)
    {
        files.WriteCurrent(current with { Probation = false });
        RecordOutcome(current.Version, UpdateOutcome.Succeeded, null);
        logger.LogInformation("Agent {Version} reported healthy: update confirmed", current.Version);
        CleanupVersions(current.Version, current.Previous?.Version);
    }

    private void RollBack(CurrentVersionFile failed, string reason)
    {
        if (failed.Previous is not { } previous)
        {
            logger.LogError("Agent {Version} failed ({Reason}) and there is no previous version to roll back to", failed.Version, reason);
            files.WriteCurrent(failed with { Probation = false });
            return;
        }

        logger.LogError("Rolling back agent {Failed} to {Previous}: {Reason}", failed.Version, previous.Version, reason);
        files.WriteCurrent(CurrentVersionFile.From(previous));
        RecordOutcome(failed.Version, UpdateOutcome.RolledBack, reason);
        _crashes = 0;
    }

    private void RecordOutcome(string version, UpdateOutcome outcome, string? error)
    {
        var state = files.ReadUpdateState();
        var failed = state.FailedVersions.ToList();
        if (outcome == UpdateOutcome.RolledBack && !failed.Contains(version))
            failed.Add(version);
        files.WriteUpdateState(new UpdateStateFile
        {
            Last = new UpdateStatusDto { Version = version, Outcome = outcome, Error = error, At = time.GetUtcNow() },
            FailedVersions = failed,
        });
    }

    private async Task DelayAfterCrashAsync(int code, TimeSpan ranFor, CancellationToken stoppingToken)
    {
        if (ranFor >= options.StableAfter)
            _crashes = 0;
        var delay = options.CrashBackoff[Math.Min(_crashes, options.CrashBackoff.Count - 1)];
        _crashes++;
        logger.LogWarning("Restarting agent in {Delay} (exit code {Code}, crash #{Count})", delay, code, _crashes);
        try
        {
            await Task.Delay(delay, time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsIntact(InstalledVersion version, out string problem)
    {
        if (!File.Exists(version.ExePath))
        {
            problem = $"{version.ExePath} not found";
            return false;
        }
        var actual = VersionFiles.Sha256Of(version.ExePath);
        if (!string.Equals(actual, version.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"sha256 of {version.ExePath} is {actual}, expected {version.Sha256}";
            return false;
        }
        problem = "";
        return true;
    }

    /// <summary>Keeps the running and the previous version; older version directories are deleted.</summary>
    private void CleanupVersions(string keep, string? keepPrevious)
    {
        foreach (var directory in Directory.EnumerateDirectories(paths.Versions))
        {
            var name = Path.GetFileName(directory);
            if (name == keep || name == keepPrevious)
                continue;
            if (!AgentVersion.IsValid(name) && !name.EndsWith(".partial", StringComparison.Ordinal))
                continue;
            try
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Removed old agent version {Directory}", directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {Directory}", directory);
            }
        }
    }
}
