using Dupli.Agent.Launcher;
using Serilog;

namespace Dupli.Agent.Server;

public interface IAgentRestarter
{
    void Restart();

    /// <summary>Hands over to the Launcher, which switches to the version staged in <c>pending.json</c>.</summary>
    void ExitForUpdate();
}

/// <summary>
/// Terminates the process with a non-zero exit code without a clean service stop: the Launcher (or, for an agent
/// run directly as a service, the service control manager's recovery action) starts it again.
/// </summary>
public sealed class ProcessExitRestarter : IAgentRestarter
{
    public const int RestartExitCode = LauncherSupervisor.RestartExitCode;

    public void Restart() => Exit(RestartExitCode);

    public void ExitForUpdate() => Exit(LauncherSupervisor.UpdateExitCode);

    private static void Exit(int code)
    {
        Log.CloseAndFlush();
        Environment.Exit(code);
    }
}
