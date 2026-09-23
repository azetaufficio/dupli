using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Install;
using Dupli.Agent.Logging;
using Dupli.Agent.Restore;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Dupli.Agent.Cli;

/// <summary>One-shot CLI command implementations. Each loads config, does its job, returns an exit code.</summary>
public static class Commands
{
    public static async Task<int> BackupAsync(AgentPaths paths, string policyName, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        using var logger = CreateLoggerFactory(paths, config.AgentName);
        var runtime = AgentRuntimeFactory.Build(paths, config, logger);

        var scheduled = FindPolicy(config, policyName);
        using var _ = SerilogSetup.PushRun($"backup:{scheduled.Policy.PolicyId}");

        var result = await runtime.PolicyRunner.RunAsync(scheduled.Policy, runtime.Repository, config.Host, ct);
        foreach (var item in result.Items)
            Console.WriteLine($"{item.SourceId}\t{item.Item}\t{item.Status}\t{item.SnapshotId}\t{item.Error}");

        return result.Status == RunStatus.Failed ? 1 : 0;
    }

    public static async Task<int> SnapshotsAsync(AgentPaths paths, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        using var logger = CreateLoggerFactory(paths, config.AgentName);
        var runtime = AgentRuntimeFactory.Build(paths, config, logger);

        var snapshots = await runtime.Engine.ListSnapshotsAsync(runtime.Repository, tags, ct);
        Console.WriteLine("id\tshort_id\ttime\thost\tpaths\ttags");
        foreach (var s in snapshots)
            Console.WriteLine($"{s.Id}\t{s.ShortId}\t{s.Time:O}\t{s.Host}\t{string.Join(',', s.Paths)}\t{string.Join(',', s.Tags)}");

        return 0;
    }

    public static async Task<int> RestoreAsync(
        AgentPaths paths, string snapshotId, string? target, IReadOnlyList<string> includes, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        using var logger = CreateLoggerFactory(paths, config.AgentName);
        var runtime = AgentRuntimeFactory.Build(paths, config, logger);

        var resolvedTarget = RestoreGuard.ResolveTarget(
            target, snapshotId, paths, config.Policies.Select(p => p.Policy).ToList());

        using var _ = SerilogSetup.PushRun($"restore:{snapshotId}");
        await runtime.Engine.RestoreAsync(new RestoreRequest(runtime.Repository, snapshotId, resolvedTarget, includes), ct);
        Console.WriteLine($"Restored snapshot {snapshotId} to {resolvedTarget}");
        return 0;
    }

    public static async Task<int> ForgetAsync(AgentPaths paths, string policyName, bool prune, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        using var logger = CreateLoggerFactory(paths, config.AgentName);
        var runtime = AgentRuntimeFactory.Build(paths, config, logger);

        var scheduled = FindPolicy(config, policyName);
        using var _ = SerilogSetup.PushRun($"forget:{scheduled.Policy.PolicyId}");

        await runtime.Engine.ForgetAsync(new ForgetRequest(
            runtime.Repository, config.Host, [BackupTags.Policy(scheduled.Policy.PolicyId)],
            scheduled.Policy.Retention.KeepDaily, scheduled.Policy.Retention.KeepWeekly, scheduled.Policy.Retention.KeepMonthly,
            prune), ct);

        Console.WriteLine($"Retention applied for policy {scheduled.Policy.PolicyId}");
        return 0;
    }

    public static async Task<int> CheckAsync(AgentPaths paths, int subsetPercent, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        using var logger = CreateLoggerFactory(paths, config.AgentName);
        var runtime = AgentRuntimeFactory.Build(paths, config, logger);

        using var _ = SerilogSetup.PushRun("check");
        var result = await runtime.Engine.CheckAsync(runtime.Repository, subsetPercent, ct);
        foreach (var line in result.Messages)
            Console.WriteLine(line);

        return result.Success ? 0 : 1;
    }

    public static Task<int> SecretSetAsync(AgentPaths paths, string name, CancellationToken ct)
    {
        paths.EnsureCreated();
        var value = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("No value provided on stdin");

        var secrets = new Secrets.DpapiSecretStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<Secrets.DpapiSecretStore>.Instance);
        secrets.Set(name, value);
        Console.WriteLine($"Secret '{name}' stored.");
        return Task.FromResult(0);
    }

    public static Task<int> InstallAsync(AgentPaths paths, string server, string token, CancellationToken ct)
    {
        using var logger = LoggerFactory.Create(b => b.AddConsole());
        return AgentInstaller.InstallAsync(paths, server, token, logger.CreateLogger("install"), ct);
    }

    public static Task<int> UninstallAsync(CancellationToken ct)
    {
        using var logger = LoggerFactory.Create(b => b.AddConsole());
        return AgentInstaller.UninstallAsync(logger.CreateLogger("uninstall"), ct);
    }

    private static ScheduledPolicy FindPolicy(AgentConfig config, string nameOrId) =>
        config.Policies.FirstOrDefault(p => p.Policy.PolicyId == nameOrId || p.Policy.Name == nameOrId)
        ?? throw new InvalidOperationException($"No policy named or identified as '{nameOrId}' in {config.AgentName}'s configuration");

    private static ILoggerFactory CreateLoggerFactory(AgentPaths paths, string agentName)
    {
        var serilogLogger = SerilogSetup.CreateLogger(paths, agentName);
        return LoggerFactory.Create(b => b.AddSerilog(serilogLogger, dispose: true));
    }
}
