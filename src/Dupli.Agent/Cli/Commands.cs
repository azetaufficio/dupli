using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Install;
using Dupli.Agent.Logging;
using Dupli.Agent.Restore;
using Dupli.Agent.Server;
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

        // Server mode: source paths come from the policy specs last received with backup jobs.
        var policies = config.Policies.Select(p => p.Policy).ToList();
        if (config.Server is not null)
            policies.AddRange(new JobLedger(paths.LedgerFile).KnownPolicies());
        var resolvedTarget = RestoreGuard.ResolveTarget(target, snapshotId, paths, policies);

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
        var value = ReadSecretValue(Console.IsInputRedirected, Console.In);
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("No value provided on stdin");

        var secrets = new Secrets.DpapiSecretStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<Secrets.DpapiSecretStore>.Instance);
        secrets.Set(name, value);
        Console.WriteLine($"Secret '{name}' stored.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Piped/redirected stdin (e.g. <c>echo secret | dupli-agent secret set name</c>) reads to EOF; an interactive
    /// console never reaches EOF on Enter, so it reads a single masked line instead.
    /// </summary>
    public static string ReadSecretValue(bool interactive, TextReader reader) =>
        interactive ? ReadSecretInteractive() : reader.ReadToEnd().TrimEnd('\r', '\n');

    /// <summary>Reads a line from an interactive console, masking each typed character as '*' so the secret never appears in clear text.</summary>
    private static string ReadSecretInteractive()
    {
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (key.KeyChar == '\0')
                continue;
            value.Append(key.KeyChar);
            Console.Write('*');
        }
    }

    /// <summary>Rotates the AgentSecret. The new value is stored before anything else can fail.</summary>
    public static async Task<int> RotateSecretAsync(AgentPaths paths, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        var server = config.Server ?? throw new InvalidOperationException("The agent is not enrolled with a server");
        using var logger = CreateLoggerFactory(paths, server.AgentId);
        var secrets = new Secrets.DpapiSecretStore(paths, logger.CreateLogger<Secrets.DpapiSecretStore>());

        var client = new ServerClient(new HttpClient(), server, secrets, TimeProvider.System, logger.CreateLogger<ServerClient>());
        var rotated = await client.RotateSecretAsync(ct);
        secrets.Set(server.AgentSecretName, rotated.AgentSecret);
        Console.WriteLine("Agent secret rotated.");
        return 0;
    }

    public static async Task<int> InstallAsync(AgentPaths paths, string? server, string? token, UpdateConfig? update, CancellationToken ct)
    {
        using var logger = LoggerFactory.Create(b => b.AddConsole());
        return await AgentInstaller.InstallAsync(paths, server, token, update, logger.CreateLogger("install"), ct);
    }

    public static async Task<int> UninstallAsync(AgentPaths paths, CancellationToken ct)
    {
        using var logger = LoggerFactory.Create(b => b.AddConsole());
        return await AgentInstaller.UninstallAsync(paths, logger.CreateLogger("uninstall"), ct);
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
