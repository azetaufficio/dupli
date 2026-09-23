using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Launcher;
using Dupli.Agent.Logging;
using Dupli.Agent.Scheduling;
using Dupli.Agent.Server;
using Dupli.Agent.Updates;
using Serilog;

namespace Dupli.Agent.Hosting;

/// <summary>
/// Long-running agent process: generic host + Windows service lifetime. Server mode (after enrollment)
/// polls the management server; otherwise the local M1 scheduler runs the policies in agent.json.
/// </summary>
public static class AgentServiceHost
{
    public const string ServiceName = "DupliAgent";

    public static async Task<int> RunAsync(AgentPaths paths, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        var logBuffer = config.Server is null ? null : new ServerLogBuffer();
        Log.Logger = SerilogSetup.CreateLogger(paths, config.Server?.AgentId ?? config.AgentName, logBuffer);
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

    private static IHost BuildHost(AgentPaths paths, AgentConfig config, ServerLogBuffer? logBuffer)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddSerilog();
        services.AddWindowsService(o => o.ServiceName = ServiceName);
        services.AddSingleton(paths);
        services.AddSingleton(config);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => AgentRuntimeFactory.Build(
            sp.GetRequiredService<AgentPaths>(),
            sp.GetRequiredService<AgentConfig>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton(new VersionFiles(paths));
        services.AddSingleton<IAgentHealthReporter, LauncherHealthReporter>();
        if (LauncherSupervisor.IsLaunched)
            services.AddHostedService<StdinShutdownService>();

        if (config.Server is { } server)
        {
            services.AddSingleton(logBuffer!);
            services.AddSingleton(new JobLedger(paths.LedgerFile));
            services.AddSingleton(sp => new ServerClient(
                new HttpClient { Timeout = TimeSpan.FromSeconds(100) },
                server,
                sp.GetRequiredService<AgentRuntime>().Secrets,
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
                return new ResticUpdater(runtime.Restic, runtime.ResticTools, runtime.Repository, paths, runtime.Processes,
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
        }
        else
        {
            services.AddHostedService<PolicyScheduler>();
            services.AddHostedService<StartupHealthService>();
        }

        return builder.Build();
    }
}
