using Dupli.Agent.Configuration;
using Dupli.Agent.Hosting;
using Dupli.Agent.Logging;
using Serilog;

namespace Dupli.Agent.Launcher;

/// <summary>
/// <c>dupli-agent launch</c>: the process the Windows service runs. Supervises the agent child (see
/// <see cref="LauncherSupervisor"/>); logs to <c>logs\launcher-*.json</c>. Exits non-zero on a fatal error so the
/// service control manager's recovery actions apply.
/// </summary>
public static class LauncherHost
{
    public static async Task<int> RunAsync(AgentPaths paths, CancellationToken ct)
    {
        Log.Logger = SerilogSetup.CreateLogger(paths, "launcher", filePrefix: "launcher");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            builder.Services.AddWindowsService(o => o.ServiceName = AgentServiceHost.ServiceName);
            // Longer than the child's stop timeout, so a service stop always waits for the agent.
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton(new VersionFiles(paths));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new LauncherOptions());
            builder.Services.AddSingleton<IChildProcessFactory, ChildProcessFactory>();
            builder.Services.AddSingleton<LauncherSupervisor>();
            builder.Services.AddSingleton<LauncherService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<LauncherService>());

            using var host = builder.Build();
            await host.RunAsync(ct);
            return host.Services.GetRequiredService<LauncherService>().Failed ? 1 : 0;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private sealed class LauncherService(
        LauncherSupervisor supervisor, IHostApplicationLifetime lifetime, ILogger<LauncherService> logger) : BackgroundService
    {
        public bool Failed { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await supervisor.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Failed = true;
                logger.LogCritical(ex, "Launcher stopped");
                lifetime.StopApplication();
            }
        }
    }
}
