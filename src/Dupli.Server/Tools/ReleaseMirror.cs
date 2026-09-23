using System.Security.Cryptography;
using Dupli.Contracts.Tools;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Tools;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Tools;

/// <summary>
/// Serves pinned releases (restic, the agent itself) to agents, so VMs only need to reach the server and S3.
/// The upstream asset is downloaded once, sha256-verified, and cached on disk per product.
/// </summary>
public sealed class ReleaseMirror(
    DupliDbContext db,
    IHttpClientFactory httpClientFactory,
    IOptions<DupliServerOptions> options,
    ILogger<ReleaseMirror> logger)
{
    public const string ResticProduct = "restic";
    public const string AgentProduct = "agent";
    public const string DefaultPlatform = "windows_amd64";

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public async Task<ToolManifestDto?> GetManifestAsync(string product, string platform, string publicBaseUrl, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Product == product && r.Platform == platform && r.IsCurrent, ct);
        return release is null ? null : ToManifest(release, publicBaseUrl);
    }

    public static ToolManifestDto ToManifest(SoftwareRelease release, string publicBaseUrl) => new()
    {
        Version = release.Version,
        DownloadUrl = $"{publicBaseUrl.TrimEnd('/')}/api/tools/{release.Product}/{release.Version}/{release.Platform}",
        Sha256 = release.Sha256,
    };

    /// <summary>Path of the verified cached asset, downloading (or copying, for a <c>file://</c> source) it on first request.</summary>
    public async Task<(string Path, string FileName)?> GetAssetAsync(string product, string version, string platform, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Product == product && r.Version == version && r.Platform == platform, ct);
        if (release is null)
            return null;

        var sourceUri = new Uri(release.SourceUrl);
        var fileName = Path.GetFileName(sourceUri.AbsolutePath);
        // Absolute on purpose: Results.File resolves relative paths against the web root, not the working directory.
        var directory = Path.GetFullPath(Path.Combine(options.Value.ToolMirrorPath, product, release.Version));
        var target = Path.Combine(directory, fileName);
        if (File.Exists(target))
            return (target, fileName);

        await DownloadLock.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
                return (target, fileName);

            Directory.CreateDirectory(directory);
            var partial = target + ".partial";
            logger.LogInformation("Mirroring {Url}", release.SourceUrl);
            if (sourceUri.IsFile)
            {
                await using var file = File.Create(partial);
                await using var source = File.OpenRead(sourceUri.LocalPath);
                await source.CopyToAsync(file, ct);
            }
            else
            {
                using var response = await httpClientFactory.CreateClient().GetAsync(release.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(partial);
                await response.Content.CopyToAsync(file, ct);
            }

            string actual;
            await using (var file = File.OpenRead(partial))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));

            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidOperationException(
                    $"sha256 mismatch for {release.SourceUrl}: expected {release.Sha256}, got {actual}");
            }

            File.Move(partial, target, overwrite: true);
            return (target, fileName);
        }
        finally
        {
            DownloadLock.Release();
        }
    }
}
