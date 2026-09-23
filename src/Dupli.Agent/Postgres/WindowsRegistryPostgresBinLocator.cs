using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Dupli.Agent.Core.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Dupli.Agent.Postgres;

/// <summary>
/// Finds pg_dump/pg_dumpall via <c>HKLM\SOFTWARE\PostgreSQL\Installations\*</c>, falling back to
/// the ImagePath of a <c>postgresql-x64-NN</c> service. Prefers an installation whose major version
/// matches the server; otherwise the closest installed major that is &gt;= the server's.
/// </summary>
public sealed partial class WindowsRegistryPostgresBinLocator(ILogger<WindowsRegistryPostgresBinLocator> logger)
    : IPostgresBinLocator
{
    public string? FindBinDirectory(int serverMajorVersion)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var candidates = DiscoverInstallations();
        if (candidates.Count == 0)
        {
            logger.LogWarning("No PostgreSQL installation found in the registry");
            return null;
        }

        if (serverMajorVersion == IPostgresBinLocator.Newest)
            return candidates.MaxBy(c => c.Major).BinDirectory;

        var exact = candidates.FirstOrDefault(c => c.Major == serverMajorVersion);
        if (exact.BinDirectory is not null)
            return exact.BinDirectory;

        var next = candidates
            .Where(c => c.Major >= serverMajorVersion)
            .OrderBy(c => c.Major)
            .FirstOrDefault();
        if (next.BinDirectory is not null)
            logger.LogWarning(
                "No pg_dump for PostgreSQL {Server} found; using {Major} instead", serverMajorVersion, next.Major);
        return next.BinDirectory;
    }

    [SupportedOSPlatform("windows")]
    private static List<(int Major, string BinDirectory)> DiscoverInstallations()
    {
        var results = new List<(int, string)>();

        using (var installations = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PostgreSQL\Installations"))
        {
            if (installations is not null)
                foreach (var name in installations.GetSubKeyNames())
                {
                    using var key = installations.OpenSubKey(name);
                    var baseDirectory = key?.GetValue("Base Directory") as string;
                    if (baseDirectory is null)
                        continue;

                    var major = ParseMajor(key?.GetValue("Version") as string) ?? ParseMajor(name);
                    var binDirectory = Path.Combine(baseDirectory, "bin");
                    if (major is not null && Directory.Exists(binDirectory))
                        results.Add((major.Value, binDirectory));
                }
        }

        if (results.Count == 0)
            results.AddRange(DiscoverFromServices());

        return results;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(int Major, string BinDirectory)> DiscoverFromServices()
    {
        using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (servicesKey is null)
            yield break;

        foreach (var name in servicesKey.GetSubKeyNames())
        {
            var match = ServiceNameRegex().Match(name);
            if (!match.Success)
                continue;

            using var service = servicesKey.OpenSubKey(name);
            var binDirectory = ExtractBinDirectory(service?.GetValue("ImagePath") as string);
            if (binDirectory is not null)
                yield return (int.Parse(match.Groups[1].Value), binDirectory);
        }
    }

    private static string? ExtractBinDirectory(string? imagePath)
    {
        if (imagePath is null)
            return null;

        // ImagePath is typically: "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" ... (quoted, with args)
        var path = imagePath.TrimStart('"');
        var end = path.IndexOf('"');
        if (end < 0)
            end = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
        if (end <= 0 || end > path.Length)
            return null;

        var exePath = path[..end];
        var directory = Path.GetDirectoryName(exePath);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    private static int? ParseMajor(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return null;
        var match = MajorVersionRegex().Match(version);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    // Service name for the official installer, e.g. "postgresql-x64-18".
    [GeneratedRegex(@"^postgresql-x64-(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceNameRegex();

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex MajorVersionRegex();
}
