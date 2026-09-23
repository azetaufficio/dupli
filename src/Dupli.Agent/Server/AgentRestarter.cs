using Serilog;

namespace Dupli.Agent.Server;

public interface IAgentRestarter
{
    void Restart();
}

/// <summary>
/// Terminates the process with a non-zero exit code without a clean service stop: the service control
/// manager treats it as a failure and applies the recovery action (restart) set at install time.
/// </summary>
public sealed class ProcessExitRestarter : IAgentRestarter
{
    public const int RestartExitCode = 75;

    public void Restart()
    {
        Log.CloseAndFlush();
        Environment.Exit(RestartExitCode);
    }
}
