using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Logging;
using Dupli.Agent.Scheduling;
using Dupli.Agent.Server;
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
            services.AddHostedService<ServerAgentLoop>();
            services.AddHostedService<ServerLogUploader>();
        }
        else
        {
            services.AddHostedService<PolicyScheduler>();
        }

        return builder.Build();
    }
}
