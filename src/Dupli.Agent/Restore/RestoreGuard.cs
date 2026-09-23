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
    /// <param name="defaultName">Folder name under the default restore root when no target is requested (snapshot or job id).</param>
    public static string ResolveTarget(
        string? requestedTarget,
        string defaultName,
        AgentPaths paths,
        IReadOnlyList<PolicySpecDto> policies)
    {
        var target = string.IsNullOrWhiteSpace(requestedTarget)
            ? DefaultTarget(paths, defaultName)
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

    private static string DefaultTarget(AgentPaths paths, string name) =>
        OperatingSystem.IsWindows()
            ? Path.Combine("C:\\DupliRestore", name)
            : Path.Combine(paths.Tmp, "restore", name);

    /// <summary>restic restore overwrites what it finds: the target must be new or empty.</summary>
    public static void EnsureEmpty(string target)
    {
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException($"Restore target '{target}' is not empty. Choose a new or empty directory.");
        if (File.Exists(target))
            throw new InvalidOperationException($"Restore target '{target}' is a file.");
    }

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
