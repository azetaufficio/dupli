using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Tools;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Tools;

/// <summary>
/// Resolves the agent and restic release an agent should run: pin (any channel) first, then the current
/// release of the agent's channel for its platform, falling back dev -&gt; beta -&gt; stable. Null when nothing
/// is registered for that platform/channel.
/// </summary>
public sealed class DesiredVersionResolver(DupliDbContext db)
{
    private static readonly Dictionary<string, string[]> ChannelFallback = new()
    {
        ["dev"] = ["dev", "beta", "stable"],
        ["beta"] = ["beta", "stable"],
        ["stable"] = ["stable"],
    };

    public async Task<(SoftwareRelease? Agent, SoftwareRelease? Restic)> ResolveAsync(Agent agent, CancellationToken ct)
    {
        var releases = await LoadCandidatesAsync([agent], ct);
        return Resolve(agent, releases);
    }

    /// <summary>One query for the whole list, to avoid N+1 in admin listings.</summary>
    public async Task<IReadOnlyDictionary<Guid, (SoftwareRelease? Agent, SoftwareRelease? Restic)>> ResolveManyAsync(
        IReadOnlyList<Agent> agents, CancellationToken ct)
    {
        var releases = await LoadCandidatesAsync(agents, ct);
        return agents.ToDictionary(a => a.Id, a => Resolve(a, releases));
    }

    private async Task<IReadOnlyList<SoftwareRelease>> LoadCandidatesAsync(IReadOnlyList<Agent> agents, CancellationToken ct)
    {
        var platforms = agents.Select(a => a.Platform).Distinct().ToList();
        return await db.Releases.AsNoTracking().Where(r => platforms.Contains(r.Platform)).ToListAsync(ct);
    }

    private static (SoftwareRelease? Agent, SoftwareRelease? Restic) Resolve(Agent agent, IReadOnlyList<SoftwareRelease> releases) =>
        (ResolveAgent(agent, releases), ResolveRestic(agent, releases));

    private static SoftwareRelease? ResolveAgent(Agent agent, IReadOnlyList<SoftwareRelease> releases)
    {
        var candidates = releases.Where(r => r.Product == ReleaseMirror.AgentProduct && r.Platform == agent.Platform).ToList();
        if (agent.PinnedAgentVersion is { } pinned)
        {
            var pinnedRelease = candidates.FirstOrDefault(r => r.Version == pinned);
            if (pinnedRelease is not null)
                return pinnedRelease;
        }

        foreach (var channel in ChannelFallback.GetValueOrDefault(agent.Channel, ["stable"]))
        {
            var current = candidates.FirstOrDefault(r => r.Channel == channel && r.IsCurrent);
            if (current is not null)
                return current;
        }
        return null;
    }

    private static SoftwareRelease? ResolveRestic(Agent agent, IReadOnlyList<SoftwareRelease> releases)
    {
        var candidates = releases.Where(r => r.Product == ReleaseMirror.ResticProduct && r.Platform == agent.Platform).ToList();
        if (agent.PinnedResticVersion is { } pinned)
        {
            var pinnedRelease = candidates.FirstOrDefault(r => r.Version == pinned);
            if (pinnedRelease is not null)
                return pinnedRelease;
        }
        return candidates.FirstOrDefault(r => r.IsCurrent);
    }
}
