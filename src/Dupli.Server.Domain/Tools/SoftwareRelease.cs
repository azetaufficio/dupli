namespace Dupli.Server.Domain.Tools;

/// <summary>Pinned third-party binary (restic) mirrored by the server. Never resolved as "latest".</summary>
public sealed class SoftwareRelease
{
    public Guid Id { get; set; }
    public required string Product { get; set; }
    public required string Version { get; set; }

    /// <summary>restic platform suffix, e.g. <c>windows_amd64</c>.</summary>
    public required string Platform { get; set; }

    /// <summary>Update channel (<c>dev</c>, <c>beta</c>, <c>stable</c>). Required for product <c>agent</c>, null for <c>restic</c>.</summary>
    public string? Channel { get; set; }

    public required string SourceUrl { get; set; }
    public required string Sha256 { get; set; }
    public bool IsCurrent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
