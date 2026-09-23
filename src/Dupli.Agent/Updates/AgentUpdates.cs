using Dupli.Agent.Core.Tools;
using Dupli.Agent.Launcher;
using Dupli.Agent.Server;
using Dupli.Contracts.Agents;

namespace Dupli.Agent.Updates;

/// <summary>What the server loop needs for updates: heartbeat fields, applying the desired state, health.</summary>
public sealed class AgentUpdates(
    AgentUpdater agent,
    ResticUpdater restic,
    ActiveResticManifest activeRestic,
    VersionFiles files,
    IAgentHealthReporter health)
{
    public string ResticVersion => activeRestic.Current.Version;

    public HeartbeatRequest Describe(HeartbeatRequest request) => request with
    {
        ResticVersion = ResticVersion,
        Platform = ResticPlatform.Current,
        LauncherManaged = agent.LauncherManaged,
        LastUpdate = files.ReadUpdateState().Last,
        ResticUpdateError = restic.LastError,
    };

    /// <summary>Runs while idle. True when a new agent version is staged and the process must exit.</summary>
    public async Task<bool> ApplyAsync(HeartbeatResponse desired, CancellationToken ct)
    {
        await restic.TryActivateAsync(desired.DesiredRestic, ct);
        return await agent.TryStageAsync(desired.DesiredAgent, ct);
    }

    public void ReportHealthy() => health.ReportHealthy();
}
