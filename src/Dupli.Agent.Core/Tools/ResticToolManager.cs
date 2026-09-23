using System.IO.Compression;
using System.Security.Cryptography;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Processes;
using Dupli.Contracts.Tools;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Core.Tools;

public interface IToolManager
{
    /// <summary>Makes sure the pinned version is installed and returns the absolute executable path.</summary>
    Task<string> EnsureInstalledAsync(ToolManifestDto manifest, CancellationToken cancellationToken);
}

public interface IResticBinaryProvider
{
    Task<string> GetPathAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Installs restic under <c>{toolsRoot}\{version}\restic.exe</c>. Never uses PATH, winget or chocolatey.
/// </summary>
public sealed class ResticToolManager(
    string toolsRoot,
    HttpClient http,
    IProcessRunner runner,
    ILogger<ResticToolManager> logger) : IToolManager
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "restic.exe" : "restic";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string GetExecutablePath(string version) => Path.Combine(toolsRoot, version, ExeName);

    public async Task<string> EnsureInstalledAsync(ToolManifestDto manifest, CancellationToken cancellationToken)
    {
        ValidateVersion(manifest.Version);
        var exe = GetExecutablePath(manifest.Version);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(exe))
                return exe;

            logger.LogInformation("Installing restic {Version} from {Url}", manifest.Version, manifest.DownloadUrl);
            Directory.CreateDirectory(toolsRoot);

            var staging = Path.Combine(toolsRoot, $".{manifest.Version}.{Guid.NewGuid():N}.partial");
            Directory.CreateDirectory(staging);
            try
            {
                var download = Path.Combine(staging, "download");
                await DownloadVerifiedAsync(manifest, download, cancellationToken);

                var stagedExe = Path.Combine(staging, ExeName);
                Extract(manifest.DownloadUrl, download, stagedExe);
                File.Delete(download);

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stagedExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                await VerifyVersionAsync(stagedExe, manifest.Version, cancellationToken);

                // Atomic publish: the version directory either exists complete or not at all.
                Directory.Move(staging, Path.Combine(toolsRoot, manifest.Version));
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }

            logger.LogInformation("restic {Version} installed at {Path}", manifest.Version, exe);
            return exe;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadVerifiedAsync(ToolManifestDto manifest, string destination, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(destination))
                await source.CopyToAsync(file, ct);
        }
        catch (HttpRequestException ex)
        {
            throw BackupException.Transient($"Download of restic {manifest.Version} failed: {ex.Message}", ex);
        }

        await using var stream = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw BackupException.Permanent(
                $"SHA256 mismatch for restic {manifest.Version}: expected {manifest.Sha256}, got {actual}");
    }

    private static void Extract(string url, string archive, string destinationExe)
    {
        var path = new Uri(url).AbsolutePath;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.SingleOrDefault(e =>
                            e.Name.StartsWith("restic", StringComparison.OrdinalIgnoreCase) &&
                            e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw BackupException.Permanent("restic executable not found in zip archive");
            entry.ExtractToFile(destinationExe);
        }
        else if (path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.OpenRead(archive);
            using var output = File.Create(destinationExe);
            BZip2.Decompress(input, output, isStreamOwner: false);
        }
        else
        {
            File.Copy(archive, destinationExe);
        }
    }

    private async Task VerifyVersionAsync(string exe, string expectedVersion, CancellationToken ct)
    {
        var output = new List<string>();
        var result = await runner.RunAsync(new ProcessSpec { FileName = exe, Arguments = ["version"] }, output.Add, ct);
        var line = output.FirstOrDefault() ?? "";
        if (result.ExitCode != 0 || !line.StartsWith($"restic {expectedVersion} ", StringComparison.Ordinal))
            throw BackupException.Permanent($"Installed restic reports '{line}', expected version {expectedVersion}");
    }

    private static void ValidateVersion(string version)
    {
        if (version.Length == 0 || version.Any(c => !(char.IsAsciiDigit(c) || c == '.')))
            throw new ArgumentException($"Invalid pinned restic version '{version}'");
    }
}

/// <summary>Resolves the restic binary for a pinned manifest, installing it on first use.</summary>
public sealed class ManagedResticBinaryProvider(IToolManager tools, ToolManifestDto manifest) : IResticBinaryProvider
{
    public Task<string> GetPathAsync(CancellationToken cancellationToken) =>
        tools.EnsureInstalledAsync(manifest, cancellationToken);
}
