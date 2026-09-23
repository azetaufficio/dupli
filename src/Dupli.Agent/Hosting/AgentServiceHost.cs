using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Logging;
using Dupli.Agent.Scheduling;
using Serilog;

namespace Dupli.Agent.Hosting;

/// <summary>Long-running agent process: generic host + Windows service lifetime + policy scheduler.</summary>
public static class AgentServiceHost
{
    public const string ServiceName = "DupliAgent";

    public static async Task<int> RunAsync(AgentPaths paths, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        Log.Logger = SerilogSetup.CreateLogger(paths, config.AgentName);
        try
        {
            using var host = BuildHost(paths, config);
            await host.RunAsync(ct);
            return 0;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IHost BuildHost(AgentPaths paths, AgentConfig config)
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
        services.AddHostedService<PolicyScheduler>();

        return builder.Build();
    }
}
