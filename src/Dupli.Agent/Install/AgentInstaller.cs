using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Dupli.Agent.Configuration;
using Dupli.Agent.Hosting;
using Dupli.Agent.Secrets;
using Dupli.Agent.Server;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Install;

/// <summary>
/// Install/uninstall: enrolls with the server (--server/--token exchanged for AgentId, secret, repository
/// password and S3 key, all stored with DPAPI), stages the current executable under
/// <c>versions\&lt;ver&gt;\</c>, writes <c>current.json</c> (the layout M4's updater will reuse) and registers
/// the LocalSystem service on Windows. Enrollment runs first, so a rejected token leaves no service behind.
/// </summary>
public static class AgentInstaller
{
    private const string ServiceName = AgentServiceHost.ServiceName;

    public static async Task<int> InstallAsync(
        AgentPaths paths, string serverUrl, string token, ILogger logger, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();

        using (var http = new HttpClient())
        {
            http.Timeout = TimeSpan.FromSeconds(60);
            var secrets = new DpapiSecretStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<DpapiSecretStore>.Instance);
            await AgentEnrollment.EnrollAsync(serverUrl, token, paths, secrets, http, logger, cancellationToken);
        }

        var version = typeof(AgentInstaller).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var versionDirectory = Path.Combine(paths.Versions, version);
        Directory.CreateDirectory(versionDirectory);

        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the current executable path");
        var targetExecutable = Path.Combine(versionDirectory, Path.GetFileName(currentExecutable));
        if (!PathsEqual(currentExecutable, targetExecutable))
            File.Copy(currentExecutable, targetExecutable, overwrite: true);

        await File.WriteAllTextAsync(
            paths.CurrentVersionFile,
            JsonSerializer.Serialize(new { version, exePath = targetExecutable }), cancellationToken);

        logger.LogInformation("Binaries staged at {Path}", targetExecutable);

        if (OperatingSystem.IsWindows())
            await CreateWindowsServiceAsync(targetExecutable, logger, cancellationToken);
        else
            logger.LogWarning("Service registration only runs on Windows; skipped on this OS");

        return 0;
    }

    public static async Task<int> UninstallAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            await RemoveWindowsServiceAsync(logger, cancellationToken);
        else
            logger.LogWarning("Service removal only runs on Windows; skipped on this OS");
        return 0;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static async Task CreateWindowsServiceAsync(string exePath, ILogger logger, CancellationToken ct)
    {
        await RunScAsync(["create", ServiceName, "binPath=", $"\"{exePath}\" run", "start=", "auto", "obj=", "LocalSystem"], logger, ct);
        // Restart on crash and on the deliberate non-zero exit of a "restart agent" job.
        await RunScAsync(["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"], logger, ct);
        await RunScAsync(["failureflag", ServiceName, "1"], logger, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RemoveWindowsServiceAsync(ILogger logger, CancellationToken ct)
    {
        await RunScAsync(["stop", ServiceName], logger, ct, allowFailure: true);
        await RunScAsync(["delete", ServiceName], logger, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunScAsync(string[] args, ILogger logger, CancellationToken ct, bool allowFailure = false)
    {
        var scPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        var psi = new ProcessStartInfo(scPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sc.exe");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"sc.exe {string.Join(' ', args)} failed with exit code {process.ExitCode}");
        logger.LogInformation("sc.exe {Args} -> exit {Code}", string.Join(' ', args), process.ExitCode);
    }
}
