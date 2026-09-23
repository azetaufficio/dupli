using System.Security.Cryptography;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Launcher;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Updates;

/// <summary>
/// Stages the agent release desired by the server: download, sha256, optional Authenticode, <c>version</c> self-check,
/// publish to <c>versions\&lt;ver&gt;\</c> and <c>pending.json</c>. The switch itself (and any rollback) is done by the
/// Launcher when this process exits with <see cref="LauncherSupervisor.UpdateExitCode"/>.
/// </summary>
public sealed class AgentUpdater(
    VersionFiles files,
    AgentPaths paths,
    HttpClient http,
    IProcessRunner runner,
    IPackageSignatureVerifier signature,
    TimeProvider time,
    ILogger<AgentUpdater> logger)
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private string? _loggedSkip;
    private (string Version, DateTimeOffset NotBefore)? _backoff;

    /// <summary>Set by tests; production reads the Launcher environment variable.</summary>
    public bool? LauncherManagedOverride { get; init; }

    public bool LauncherManaged => LauncherManagedOverride ?? LauncherSupervisor.IsLaunched;

    /// <summary>True when a new version is staged and the process must exit for the Launcher to switch.</summary>
    public async Task<bool> TryStageAsync(ToolManifestDto? desired, CancellationToken ct)
    {
        if (desired is null || desired.Version == AgentVersion.Current)
            return false;

        if (!AgentVersion.IsValid(desired.Version))
        {
            LogSkipOnce(desired.Version, "Server wants agent version '{Version}', which is not a valid version string");
            return false;
        }
        if (files.ReadUpdateState().FailedVersions.Contains(desired.Version))
        {
            LogSkipOnce(desired.Version, "Agent {Version} was rolled back earlier: not retried");
            return false;
        }
        if (!LauncherManaged)
        {
            LogSkipOnce(desired.Version, "Agent {Version} available, but this process was not started by the Launcher: update skipped");
            return false;
        }
        if (_backoff is { } b && b.Version == desired.Version && time.GetUtcNow() < b.NotBefore)
            return false;

        try
        {
            var staged = await StageAsync(desired, ct);
            files.WritePending(staged);
            logger.LogWarning("Agent {Version} staged at {Path}: handing over to the Launcher", staged.Version, staged.ExePath);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            _backoff = (desired.Version, time.GetUtcNow() + RetryAfterFailure);
            logger.LogWarning(ex, "Update to agent {Version} failed, retrying in {Delay}", desired.Version, RetryAfterFailure);
            return false;
        }
    }

    private async Task<InstalledVersion> StageAsync(ToolManifestDto desired, CancellationToken ct)
    {
        var directory = files.VersionDirectory(desired.Version);
        var exe = Path.Combine(directory, VersionFiles.ExeFileName);
        var sha = desired.Sha256.ToLowerInvariant();

        // Already staged by an earlier attempt (e.g. the switch was interrupted): reuse it if intact.
        if (File.Exists(exe) && VersionFiles.Sha256Of(exe) == sha)
            return new InstalledVersion { Version = desired.Version, ExePath = exe, Sha256 = sha };

        var staging = Path.Combine(paths.Versions, $".{desired.Version}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        try
        {
            var stagedExe = Path.Combine(staging, VersionFiles.ExeFileName);
            logger.LogInformation("Downloading agent {Version} from {Url}", desired.Version, desired.DownloadUrl);
            using (var response = await http.GetAsync(desired.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(stagedExe);
                await response.Content.CopyToAsync(file, ct);
            }

            string actual;
            await using (var file = File.OpenRead(stagedExe))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (actual != sha)
                throw new InvalidDataException($"sha256 mismatch for agent {desired.Version}: expected {sha}, got {actual}");

            signature.Verify(stagedExe);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stagedExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await VerifyReportedVersionAsync(stagedExe, desired.Version, ct);

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            Directory.Move(staging, directory);
            return new InstalledVersion { Version = desired.Version, ExePath = exe, Sha256 = sha };
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>The package must start and report the expected version: catches wrong platform or a corrupt build.</summary>
    private async Task VerifyReportedVersionAsync(string exe, string expected, CancellationToken ct)
    {
        var output = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessSpec { FileName = exe, Arguments = ["version"] }, output.Add, timeout.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidDataException($"Agent {expected} package does not start: {ex.Message}", ex);
        }
        var reported = output.FirstOrDefault()?.Trim();
        if (result.ExitCode != 0 || reported != expected)
            throw new InvalidDataException($"Agent package reports version '{reported}' (exit {result.ExitCode}), expected {expected}");
    }

    private void LogSkipOnce(string version, string message)
    {
        if (_loggedSkip == version)
            return;
        _loggedSkip = version;
#pragma warning disable CA2254 // Constant templates chosen above.
        logger.LogInformation(message, version);
#pragma warning restore CA2254
    }
}
