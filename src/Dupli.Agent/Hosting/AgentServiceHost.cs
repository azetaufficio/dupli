using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Launcher;
using Dupli.Agent.Logging;
using Dupli.Agent.Secrets;
using Dupli.Agent.Server;
using Dupli.Agent.Updates;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Dupli.Agent.Hosting;

/// <summary>
/// Long-running agent process: generic host + Windows service lifetime. Always server-driven: the agent has
/// no standalone mode, so <c>run</c> requires an enrolled <c>agent.json</c> (see <c>install --server --token</c>).
/// </summary>
public static class AgentServiceHost
{
    public const string ServiceName = "DupliAgent";

    public static async Task<int> RunAsync(AgentPaths paths, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        if (config.Server is null)
            throw new InvalidOperationException(
                "Agent not registered: run 'dupli-agent install --server <url> --token <token>' first");

        var logBuffer = new ServerLogBuffer();
        Log.Logger = SerilogSetup.CreateLogger(paths, config.Server.AgentId, logBuffer);
        try
        {
            using var host = BuildHost(paths, config, logBuffer);
            await host.RunAsync(ct);
            return 0;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IHost BuildHost(AgentPaths paths, AgentConfig config, ServerLogBuffer logBuffer)
    {
        var server = config.Server!;
        paths.EnsureCreated();

        // Once per process start: only the agent's identity towards the server is ever kept on disk from here on.
        using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(dispose: false));
        var secrets = new DpapiSecretStore(paths, bootstrapLoggerFactory.CreateLogger<DpapiSecretStore>());
        CleanupLocalSecrets(paths, secrets);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddSerilog();
        services.AddWindowsService(o => o.ServiceName = ServiceName);
        services.AddSingleton(paths);
        services.AddSingleton(config);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretStore>(secrets);
        services.AddSingleton(sp => AgentRuntimeFactory.Build(
            sp.GetRequiredService<AgentPaths>(),
            sp.GetRequiredService<AgentConfig>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ISecretStore>()));

        services.AddSingleton(new VersionFiles(paths));
        services.AddSingleton<IAgentHealthReporter, LauncherHealthReporter>();
        if (LauncherSupervisor.IsLaunched)
            services.AddHostedService<StdinShutdownService>();

        services.AddSingleton(logBuffer);
        services.AddSingleton(new JobLedger(paths.LedgerFile));
        services.AddSingleton(sp => new ServerClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(100) },
            server,
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ServerClient>>()));
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<IAgentRestarter, ProcessExitRestarter>();
        services.AddSingleton<IPackageSignatureVerifier>(new AuthenticodeVerifier(config.Update));
        services.AddSingleton(sp => new AgentUpdater(
            sp.GetRequiredService<VersionFiles>(),
            paths,
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            sp.GetRequiredService<AgentRuntime>().Processes,
            sp.GetRequiredService<IPackageSignatureVerifier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AgentUpdater>>()));
        services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<AgentRuntime>();
            return new ResticUpdater(runtime.Restic, runtime.ResticTools, paths,
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>());
        });
        services.AddSingleton(sp => new AgentUpdates(
            sp.GetRequiredService<AgentUpdater>(),
            sp.GetRequiredService<ResticUpdater>(),
            sp.GetRequiredService<AgentRuntime>().Restic,
            sp.GetRequiredService<VersionFiles>(),
            sp.GetRequiredService<IAgentHealthReporter>()));
        services.AddHostedService<ServerAgentLoop>();
        services.AddHostedService<ServerLogUploader>();

        return builder.Build();
    }

    /// <summary>
    /// Purges every secret but the agent's identity: business credentials (repository, PostgreSQL) are fetched
    /// per job now and never persisted. Also removes a leftover <c>storage-credentials.json</c> from an older
    /// agent version. Runs once per process start; only the names removed are logged, never a value.
    /// Public (not just called from <see cref="RunAsync"/>) so it is unit-testable without a full host.
    /// </summary>
    public static void CleanupLocalSecrets(AgentPaths paths, DpapiSecretStore secrets)
    {
        var removed = secrets.Names().Where(name => name != SecretNames.AgentSecret).ToList();
        foreach (var name in removed)
            secrets.Delete(name);

        if (File.Exists(paths.StorageCredentialsFile))
        {
            File.Delete(paths.StorageCredentialsFile);
            removed.Add(Path.GetFileName(paths.StorageCredentialsFile));
        }

        if (removed.Count > 0)
            Log.Information("Removed local secrets no longer kept by the agent: {Names}", removed);
    }
}
