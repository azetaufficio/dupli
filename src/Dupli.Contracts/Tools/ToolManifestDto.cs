namespace Dupli.Contracts.Tools;

/// <summary>Pinned tool release. Versions are never resolved as "latest".</summary>
public sealed record ToolManifestDto
{
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }

    /// <summary>SHA256 (hex) of the downloaded artifact (archive or executable).</summary>
    public required string Sha256 { get; init; }
}
