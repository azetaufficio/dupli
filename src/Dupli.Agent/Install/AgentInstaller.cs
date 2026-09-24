using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Dupli.Agent.Configuration;
using Dupli.Agent.Hosting;
using Dupli.Agent.Launcher;
using Dupli.Agent.Secrets;
using Dupli.Agent.Server;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Install;

/// <summary>
/// Install/uninstall. With a token: enrolls with the server (AgentId, secret, repository password and S3 key, all
/// stored with DPAPI) first, so a rejected token leaves nothing behind. Then, also without a token (repair/upgrade of
/// an enrolled machine): stages the current executable under <c>versions\&lt;ver&gt;\</c>, copies it as the Launcher
/// (<c>%ProgramFiles%\Dupli\Launcher</c>), writes <c>current.json</c>, restricts the ACL of the agent home and
/// registers the LocalSystem service running <c>launch</c>.
/// </summary>
public static class AgentInstaller
{
    private const string ServiceName = AgentServiceHost.ServiceName;

    public static async Task<int> InstallAsync(
        AgentPaths paths, string? serverUrl, string? token, UpdateConfig? update, ILogger logger, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (OperatingSystem.IsWindows())
            RestrictAgentHome(paths.Root);

        if (token is not null)
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            var secrets = new DpapiSecretStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<DpapiSecretStore>.Instance);
            await AgentEnrollment.EnrollAsync(serverUrl!, token, paths, secrets, http, logger, cancellationToken);
        }
        else if (!File.Exists(paths.ConfigFile))
        {
            throw new InvalidOperationException($"{paths.ConfigFile} not found: the first install needs --server and --token");
        }

        if (update is not null)
            await SaveUpdateConfigAsync(paths, update, cancellationToken);

        // Repair/upgrade: the running Launcher holds its executable open.
        var serviceExists = OperatingSystem.IsWindows() && ServiceExists();
        if (serviceExists && OperatingSystem.IsWindows())
            StopService(logger);

        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the current executable path");
        var files = new VersionFiles(paths);
        var installed = StageVersion(files, currentExecutable);
        var launcher = CopyFile(currentExecutable, paths.LauncherDirectory);

        // A repair after updates keeps the running version as the rollback target.
        var previous = files.ReadCurrent();
        files.WriteCurrent(CurrentVersionFile.From(installed,
            previous is not null && previous.Version != installed.Version ? previous.ToInstalled() : previous?.Previous));
        files.DeletePending();
        logger.LogInformation("Agent {Version} staged at {Path}, Launcher at {Launcher}", installed.Version, installed.ExePath, launcher);

        if (OperatingSystem.IsWindows())
        {
            await ConfigureWindowsServiceAsync(launcher, serviceExists, logger, cancellationToken);
            StartService(logger);
        }
        else
        {
            logger.LogWarning("Service registration only runs on Windows; run '{Launcher} launch' under your supervisor", launcher);
        }

        return 0;
    }

    public static async Task<int> UninstallAsync(AgentPaths paths, ILogger logger, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            await RemoveWindowsServiceAsync(logger, cancellationToken);
        else
            logger.LogWarning("Service removal only runs on Windows; skipped on this OS");

        if (Directory.Exists(paths.LauncherDirectory) && !IsInside(Environment.ProcessPath, paths.LauncherDirectory))
            Directory.Delete(paths.LauncherDirectory, recursive: true);
        logger.LogInformation("Agent data kept in {Root}", paths.Root);
        return 0;
    }

    private static InstalledVersion StageVersion(VersionFiles files, string currentExecutable)
    {
        var version = AgentVersion.Current;
        var directory = files.VersionDirectory(version);
        var exe = CopyFile(currentExecutable, directory);
        return new InstalledVersion { Version = version, ExePath = exe, Sha256 = VersionFiles.Sha256Of(exe) };
    }

    /// <summary>Copies the executable as <c>dupli-agent(.exe)</c> into <paramref name="directory"/> (no-op when it already runs from there).</summary>
    private static string CopyFile(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, VersionFiles.ExeFileName);
        if (!PathsEqual(source, target))
        {
            var temp = target + ".tmp";
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return target;
    }

    private static async Task SaveUpdateConfigAsync(AgentPaths paths, UpdateConfig update, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile) with { Update = update };
        var temp = paths.ConfigFile + ".tmp";
        await File.WriteAllTextAsync(temp, AgentConfigLoader.Serialize(config), ct);
        File.Move(temp, paths.ConfigFile, overwrite: true);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string? file, string directory) =>
        file is not null && Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SYSTEM + Administrators full control, Users read, no inheritance from ProgramData (where any user may create
    /// folders): otherwise a user could pre-create <c>versions\&lt;future&gt;\</c> and plant an executable.
    /// <c>config\secrets</c> keeps its own stricter ACL.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictAgentHome(string root)
    {
        var info = new DirectoryInfo(root);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool ServiceExists() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

    [SupportedOSPlatform("windows")]
    private static void StopService(ILogger logger)
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped)
            return;
        logger.LogInformation("Stopping {Service}", ServiceName);
        if (service.Status is not ServiceControllerStatus.StopPending)
            service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90));
    }

    [SupportedOSPlatform("windows")]
    private static void StartService(ILogger logger)
    {
        using var service = new ServiceController(ServiceName);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        logger.LogInformation("{Service} started", ServiceName);
    }

    [SupportedOSPlatform("windows")]
    private static async Task ConfigureWindowsServiceAsync(string launcherPath, bool exists, ILogger logger, CancellationToken ct)
    {
        var binPath = $"\"{launcherPath}\" launch";
        if (exists)
            await RunScAsync(["config", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem"], logger, ct);
        else
            await RunScAsync(["create", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem"], logger, ct);
        // Restart the Launcher if it crashes or exits with an error.
        await RunScAsync(["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"], logger, ct);
        await RunScAsync(["failureflag", ServiceName, "1"], logger, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RemoveWindowsServiceAsync(ILogger logger, CancellationToken ct)
    {
        if (!ServiceExists())
            return;
        StopService(logger);
        await RunScAsync(["delete", ServiceName], logger, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunScAsync(string[] args, ILogger logger, CancellationToken ct)
    {
        var scPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        var psi = new ProcessStartInfo(scPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sc.exe");
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"sc.exe {string.Join(' ', args)} failed with exit code {process.ExitCode}");
        logger.LogInformation("sc.exe {Args} -> exit {Code}", string.Join(' ', args), process.ExitCode);
    }
}
