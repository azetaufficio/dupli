using Dupli.Agent.Configuration;
using Serilog;
using Serilog.Context;
using Serilog.Core;

namespace Dupli.Agent.Logging;

public static class SerilogSetup
{
    /// <summary>Rolling JSON file in <c>logs\</c> + console. AgentId is a constant enricher;
    /// JobId/PolicyId/SourceId/RunId are pushed per execution via <see cref="LogContext"/>.</summary>
    public static Logger CreateLogger(AgentPaths paths, string agentName)
    {
        paths.EnsureCreated();
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("AgentId", agentName)
            .WriteTo.Console()
            .WriteTo.File(
                new Serilog.Formatting.Compact.CompactJsonFormatter(),
                Path.Combine(paths.Logs, "agent-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();
    }

    /// <summary>Pushes JobId/RunId for the duration of one command or scheduled execution.</summary>
    public static IDisposable PushRun(string jobId) =>
        new CompositeDisposable(
            LogContext.PushProperty("JobId", jobId),
            LogContext.PushProperty("RunId", Guid.NewGuid()));

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var d in disposables)
                d.Dispose();
        }
    }
}
