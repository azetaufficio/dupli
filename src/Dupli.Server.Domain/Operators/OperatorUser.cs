namespace Dupli.Server.Domain.Operators;

/// <summary>Ordered: a higher role includes every permission of the lower ones.</summary>
public enum OperatorRole
{
    /// <summary>Read-only: dashboard, agents, policies, jobs, logs, alerts. No snapshot contents.</summary>
    Viewer = 0,

    /// <summary>Runs the backups: policies, connections, agents, jobs, snapshot browsing and restores.</summary>
    Operator = 1,

    /// <summary>Everything, plus operator users, storage target writes and releases.</summary>
    Owner = 2,
}

/// <summary>
/// A person allowed to use the web UI. Invited by email; the first Entra ID sign-in binds the row to the
/// immutable (<see cref="TenantId"/>, <see cref="ObjectId"/>) pair, which every later sign-in matches on.
/// </summary>
public sealed class OperatorUser
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public OperatorRole Role { get; set; }

    /// <summary>Entra ID <c>tid</c>/<c>oid</c>; null until the invitation has been used.</summary>
    public string? TenantId { get; set; }
    public string? ObjectId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>UI language ("en", "it"); null means unset, the browser's language is used instead.</summary>
    public string? Language { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }

    public bool IsBound => ObjectId is not null;
    public bool IsActive => DisabledAt is null;
}
