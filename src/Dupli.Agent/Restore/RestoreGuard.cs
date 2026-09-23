using Dupli.Agent.Configuration;
using Dupli.Contracts.Policies;

namespace Dupli.Agent.Restore;

/// <summary>
/// A restore must never be able to overwrite production data: it always goes to an
/// alternative location, and a target that coincides with (or nests) a configured source path
/// is rejected outright.
/// </summary>
public static class RestoreGuard
{
    public static string ResolveTarget(
        string? requestedTarget,
        string snapshotId,
        AgentPaths paths,
        IReadOnlyList<PolicySpecDto> policies)
    {
        var target = string.IsNullOrWhiteSpace(requestedTarget)
            ? DefaultTarget(paths, snapshotId)
            : Path.GetFullPath(requestedTarget);

        var sourcePaths = policies
            .SelectMany(p => p.Sources)
            .OfType<DirectorySourceDto>()
            .SelectMany(d => d.Paths)
            .Select(Path.GetFullPath);

        foreach (var source in sourcePaths)
            if (Overlaps(target, source))
                throw new InvalidOperationException(
                    $"Restore target '{target}' coincides with (or contains, or is contained by) " +
                    $"the source path '{source}'. Choose a different --target.");

        return target;
    }

    private static string DefaultTarget(AgentPaths paths, string snapshotId) =>
        OperatingSystem.IsWindows()
            ? Path.Combine("C:\\DupliRestore", snapshotId)
            : Path.Combine(paths.Tmp, "restore", snapshotId);

    private static bool Overlaps(string target, string source)
    {
        var a = Normalize(target);
        var b = Normalize(source);
        return a == b
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
