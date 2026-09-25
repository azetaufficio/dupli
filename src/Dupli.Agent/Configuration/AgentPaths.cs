namespace Dupli.Agent.Configuration;

/// <summary>
/// Well-known on-disk layout under the agent's data root. Ready for the M4 self-update
/// layout: versions are published side by side, only <c>current.json</c> changes.
/// </summary>
public sealed class AgentPaths
{
    public string Root { get; }
    public string Versions { get; }
    public string Tools { get; }
    public string ResticTools { get; }
    public string Logs { get; }
    public string Config { get; }
    public string Secrets { get; }
    public string Tmp { get; }
    public string Cache { get; }

    public string ConfigFile => Path.Combine(Config, "agent.json");
    public string CurrentVersionFile => Path.Combine(Versions, "current.json");
    public string LedgerFile => Path.Combine(Config, "agent.db");

    /// <summary>Active restic release after a restic update; overrides the manifest received at enrollment.</summary>
    public string ResticStateFile => Path.Combine(Config, "restic.json");

    /// <summary>S3 credentials version currently applied by this agent (see <c>StorageCredentialsState</c>).</summary>
    public string StorageCredentialsFile => Path.Combine(Config, "storage-credentials.json");

    /// <summary>
    /// Stable copy of the executable run by the service as Launcher: <c>%ProgramFiles%\Dupli\Launcher</c> on Windows
    /// (admin-only writable), <c>{root}/launcher</c> elsewhere.
    /// </summary>
    public string LauncherDirectory { get; }

    public AgentPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("DUPLI_HOME") ?? DefaultRoot();
        Versions = Path.Combine(Root, "versions");
        Tools = Path.Combine(Root, "tools");
        ResticTools = Path.Combine(Tools, "restic");
        Logs = Path.Combine(Root, "logs");
        Config = Path.Combine(Root, "config");
        Secrets = Path.Combine(Config, "secrets");
        Tmp = Path.Combine(Root, "tmp");
        Cache = Path.Combine(Root, "cache");
        LauncherDirectory = OperatingSystem.IsWindows() && root is null && Environment.GetEnvironmentVariable("DUPLI_HOME") is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dupli", "Launcher")
            : Path.Combine(Root, "launcher");
    }

    /// <summary>Creates every well-known subdirectory (idempotent).</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Versions, Tools, ResticTools, Logs, Config, Secrets, Tmp, Cache })
            Directory.CreateDirectory(dir);
    }

    // %ProgramData%\Dupli on Windows. Non-Windows has no real equivalent: only used in
    // dev/tests, where DUPLI_HOME is expected to be set; this is a last-resort fallback.
    private static string DefaultRoot() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dupli")
            : Path.Combine(Path.GetTempPath(), "Dupli");
}
