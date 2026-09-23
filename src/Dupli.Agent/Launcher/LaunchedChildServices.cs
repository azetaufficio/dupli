using Dupli.Agent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Launcher;

/// <summary>
/// Runs only in a child of the Launcher: stdin EOF (Launcher stopping, or dead) stops the host gracefully, the same
/// path a service stop takes. A running job stays Running in the ledger and is reported as interrupted at restart.
/// </summary>
public sealed class StdinShutdownService(IHostApplicationLifetime lifetime, ILogger<StdinShutdownService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Dedicated thread: reads on an anonymous pipe are not reliably cancellable.
        Task.Factory.StartNew(() =>
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[256];
            while (!stoppingToken.IsCancellationRequested && stdin.Read(buffer, 0, buffer.Length) > 0)
            {
            }
            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Launcher closed stdin: stopping");
                lifetime.StopApplication();
            }
        }, stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

/// <summary>Tells the Launcher that this version works (ends the post-update probation).</summary>
public interface IAgentHealthReporter
{
    void ReportHealthy();
}

public sealed class LauncherHealthReporter(VersionFiles files, TimeProvider time, ILogger<LauncherHealthReporter> logger) : IAgentHealthReporter
{
    private int _reported;

    public void ReportHealthy()
    {
        if (!LauncherSupervisor.IsLaunched || Interlocked.Exchange(ref _reported, 1) == 1)
            return;
        try
        {
            files.WriteHealth(new HealthFile { Version = AgentVersion.Current, Pid = Environment.ProcessId, At = time.GetUtcNow() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _reported = 0;
            logger.LogWarning(ex, "Could not write the health file");
        }
    }
}

/// <summary>Local (M1) mode: healthy once the host is up, there is no server cycle to wait for.</summary>
public sealed class StartupHealthService(IAgentHealthReporter health) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        health.ReportHealthy();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
