namespace Dupli.Server.Domain.Agents;

public enum AgentStatus
{
    /// <summary>Created by an operator, waiting for an agent to enroll with a token.</summary>
    Pending,
    Active,
    Disabled,
}

/// <summary>One Host. Owns exactly one restic repository at <c>{StorageTarget}/{StoragePrefix}</c>.</summary>
public sealed class Agent
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Pending;

    public string? Hostname { get; set; }
    public string? MachineId { get; set; }
    public string? OsVersion { get; set; }
    public string? Version { get; set; }
    public string? ResticVersion { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastBackupAt { get; set; }
    public long? FreeDiskSpace { get; set; }

    /// <summary>Release platform (<c>windows_amd64</c>, <c>linux_amd64</c>, <c>linux_arm64</c>).</summary>
    public string Platform { get; set; } = "windows_amd64";

    /// <summary>Update channel: <c>dev</c>, <c>beta</c> or <c>stable</c> (default).</summary>
    public string Channel { get; set; } = "stable";

    /// <summary>Forces a specific agent version regardless of channel. Downgrade allowed.</summary>
    public string? PinnedAgentVersion { get; set; }

    /// <summary>Forces a specific restic version regardless of the current release.</summary>
    public string? PinnedResticVersion { get; set; }

    /// <summary>True when the process is supervised by the Launcher, i.e. it can apply updates.</summary>
    public bool LauncherManaged { get; set; }

    /// <summary>Outcome (<c>UpdateOutcome</c> name) of the last agent update attempted by the Launcher.</summary>
    public string? LastUpdateOutcome { get; set; }
    public string? LastUpdateVersion { get; set; }
    public string? LastUpdateError { get; set; }
    public DateTimeOffset? LastUpdateAt { get; set; }

    /// <summary>Why the desired restic version could not be activated (null when active or not attempted).</summary>
    public string? ResticUpdateError { get; set; }

    /// <summary>SHA-256 (hex) of the high-entropy agent secret. The secret itself is never stored.</summary>
    public string? SecretHash { get; set; }
    public DateTimeOffset? SecretRotatedAt { get; set; }

    public Guid StorageTargetId { get; set; }
    public StorageTarget? StorageTarget { get; set; }
    public required string StoragePrefix { get; set; }
    public required string S3AccessKeyId { get; set; }

    /// <summary>Data Protection payloads (escrow). Decrypted only to deliver them at enrollment.</summary>
    public required string S3SecretKeyProtected { get; set; }
    public required string RepositoryPasswordProtected { get; set; }

    /// <summary>Bumped by an operator's PUT of new S3 credentials; the agent applies them and reports the
    /// version back in its heartbeat as <see cref="S3CredentialsAppliedVersion"/>.</summary>
    public int S3CredentialsVersion { get; set; } = 1;
    public int? S3CredentialsAppliedVersion { get; set; }
    public DateTimeOffset? S3CredentialsUpdatedAt { get; set; }
    public string? S3CredentialsUpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }

    public DateTimeOffset? LastRetentionScheduledFor { get; set; }
    public DateTimeOffset? LastCheckScheduledFor { get; set; }
    public DateTimeOffset? LastRestoreTestScheduledFor { get; set; }

    /// <summary>Since when <see cref="Version"/> stopped matching the version DesiredVersionResolver resolves.
    /// Null when aligned (or not yet compared).</summary>
    public DateTimeOffset? OutdatedSince { get; set; }

    public bool IsOnline(DateTimeOffset now, TimeSpan offlineAfter) =>
        Status == AgentStatus.Active && LastHeartbeatAt is { } hb && now - hb <= offlineAfter;
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>SHA-256 (hex) of the token; the token is shown once to the operator.</summary>
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;
}

/// <summary>S3 endpoint + bucket shared by many agents; each agent writes under its own prefix with its own key.</summary>
public sealed class StorageTarget
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Endpoint { get; set; }
    public required string Bucket { get; set; }
    public string? Region { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A reusable PostgreSQL connection on an agent, referenced by policy sources via <c>ConnectionId</c>.
/// The password itself never appears here: only the name of the secret holding it in the agent's local store.
/// </summary>
public sealed class PgConnection
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public required string Name { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public required string Username { get; set; }
    public required string PasswordSecret { get; set; }
    public string? BinDirectory { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
