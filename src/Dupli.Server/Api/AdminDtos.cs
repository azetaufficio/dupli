using System.Text.Json.Serialization;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Domain.Operators;

namespace Dupli.Server.Api;

// Operator-facing DTOs (admin API, consumed by the web UI). Not shared with the agent.

public sealed record CreateStorageTargetRequest(string Name, string Endpoint, string Bucket, string? Region);

public sealed record StorageTargetDto(Guid Id, string Name, string Endpoint, string Bucket, string? Region);

public sealed class CreateAgentRequest
{
    public required string Name { get; init; }
    public required Guid StorageTargetId { get; init; }
    public required string StoragePrefix { get; init; }
    public required string S3AccessKeyId { get; init; }
    public required string S3SecretAccessKey { get; init; }

    /// <summary>Existing repository password (e.g. an M1 agent). Generated when omitted.</summary>
    public string? RepositoryPassword { get; init; }

    public override string ToString() => $"CreateAgentRequest({Name})";
}

public sealed record AgentDto(
    Guid Id,
    string Name,
    AgentStatus Status,
    bool Online,
    string? Hostname,
    string? OsVersion,
    string? Version,
    string? ResticVersion,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastBackupAt,
    long? FreeDiskSpace,
    Guid StorageTargetId,
    string StoragePrefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EnrolledAt,
    string Platform,
    string Channel,
    string? PinnedAgentVersion,
    string? PinnedResticVersion,
    bool LauncherManaged,
    string? DesiredAgentVersion,
    string? DesiredResticVersion,
    string? LastUpdateVersion,
    string? LastUpdateOutcome,
    string? LastUpdateError,
    DateTimeOffset? LastUpdateAt,
    string? ResticUpdateError,

    /// <summary>Never the secret access key: only enough to tell the operator which key is configured.</summary>
    string S3AccessKeyId,
    int S3CredentialsVersion,

    /// <summary>Version the agent last reported as applied. Behind <see cref="S3CredentialsVersion"/>: it has
    /// not picked up the latest credentials yet (or is too old to report it at all, in which case this is null).</summary>
    int? S3CredentialsAppliedVersion,
    DateTimeOffset? S3CredentialsUpdatedAt);

/// <summary>Channel/pins an operator can change from the UI.</summary>
public sealed record UpdateAgentSettingsRequest(string Channel, string? PinnedAgentVersion, string? PinnedResticVersion);

/// <summary>Owner only. Verified against the repository unless <see cref="SkipVerification"/> (e.g. the bucket
/// or the agent VM is unreachable from the server right now).</summary>
public sealed class UpdateAgentStorageCredentialsRequest
{
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public bool SkipVerification { get; init; }

    public override string ToString() => "UpdateAgentStorageCredentialsRequest";
}

public sealed record EnrollmentTokenDto(string Token, DateTimeOffset ExpiresAt);

public sealed record PolicyRequest
{
    public required string Name { get; init; }
    public required string Cron { get; init; }
    public string TimeZone { get; init; } = "Europe/Rome";
    public bool Enabled { get; init; } = true;
    public RetentionDto Retention { get; init; } = new();
    public required IReadOnlyList<PolicySourceDto> Sources { get; init; }
}

public sealed record PolicyDto(
    Guid Id,
    Guid AgentId,
    string Name,
    string Cron,
    string TimeZone,
    bool Enabled,
    RetentionDto Retention,
    IReadOnlyList<PolicySourceDto> Sources,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastScheduledFor);

/// <summary>
/// Admin-facing source shape, stored as <c>BackupSource.Spec</c>. Distinct from the agent contract
/// <c>BackupSourceDto</c>: a postgres source here references a <see cref="PgConnectionDto"/> by id instead
/// of embedding host/port/credentials, which are resolved server-side when building the agent job payload.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PolicyDirectorySourceDto), "directory")]
[JsonDerivedType(typeof(PolicyPostgresSourceDto), "postgres")]
public abstract record PolicySourceDto
{
    public required string SourceId { get; init; }
}

public sealed record PolicyDirectorySourceDto : PolicySourceDto
{
    public required IReadOnlyList<string> Paths { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = [];
}

public sealed record PolicyPostgresSourceDto : PolicySourceDto
{
    public required Guid ConnectionId { get; init; }
    public DatabaseSelection DatabaseSelection { get; init; } = DatabaseSelection.AllExcept;
    public IReadOnlyList<string> ExcludeDatabases { get; init; } = [];
    public IReadOnlyList<string> IncludeDatabases { get; init; } = [];
    public bool IncludeGlobals { get; init; } = true;
}

public sealed record PgConnectionRequest
{
    public required string Name { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public required string Username { get; init; }

    public string? BinDirectory { get; init; }

    /// <summary>Write-only: when set, replaces the escrowed password. Never returned by any endpoint.</summary>
    public string? Password { get; init; }

    public override string ToString() => $"PgConnectionRequest({Name})";
}

public sealed record PgConnectionDto(
    Guid Id,
    Guid AgentId,
    string Name,
    string Host,
    int Port,
    string Username,
    string PasswordSecret,
    string? BinDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,

    /// <summary>Whether a password is currently escrowed under <see cref="PasswordSecret"/>. The value itself
    /// is never part of this DTO.</summary>
    bool PasswordSet);

public sealed record RunSystemJobRequest(JobType Type);

/// <summary>Keyset page: <see cref="Next"/> is an opaque cursor for the next call's <c>before</c>, or null
/// when this was the last page.</summary>
public sealed record PagedDto<T>(IReadOnlyList<T> Items, string? Next);

public sealed record JobDto(
    Guid Id,
    Guid AgentId,

    /// <summary>Name of <see cref="AgentId"/> at the time of the query. The agent always still exists while
    /// the job row does (cascade delete), so this is never null.</summary>
    string AgentName,
    Guid? PolicyId,
    string? PolicyName,
    JobType Type,
    JobTrigger Trigger,
    JobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ScheduledAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool CancelRequested,
    string? Error,
    IReadOnlyList<JobItemResultDto> Items);

public sealed record RunDto(
    Guid Id,
    Guid JobId,
    Guid PolicyId,
    string? PolicyName,
    Guid AgentId,
    string AgentName,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Status,
    long BytesProcessed,
    long BytesAdded,
    IReadOnlyList<JobItemResultDto> Items,
    string? ErrorMessage);

public sealed record LogDto(
    long Id,
    Guid AgentId,
    string AgentName,
    Guid? JobId,
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception);

public sealed record AlertDto(
    Guid Id,
    AlertKind Kind,
    string SubjectKey,
    Guid? AgentId,
    string? AgentName,
    Guid? PolicyId,
    string? PolicyName,
    string Message,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt);

public sealed record CreateReleaseRequest(string Version, string Platform, string SourceUrl, string Sha256, bool MakeCurrent = true);

public sealed record CreateAgentReleaseRequest(string Version, string Platform, string Channel, string SourceUrl, string Sha256, bool MakeCurrent = true);

public sealed record ImportAgentReleaseRequest(string Version, string Channel, bool MakeCurrent = true);

public sealed record ReleaseDto(
    Guid Id, string Product, string Version, string Platform, string? Channel, string SourceUrl, string Sha256, bool IsCurrent, DateTimeOffset CreatedAt);

public sealed record DashboardDto(DashboardCountersDto Counters, IReadOnlyList<DashboardAgentDto> Agents);

public sealed record DashboardCountersDto(int Online, int Offline, int Pending, int BackupFailed, int BackupRunning, int OpenAlerts);

/// <param name="LastRunStatus"><c>JobOutcome</c> name of the agent's most recent backup run.</param>
/// <param name="RunningJob"><c>JobType</c> name of the active job, if any.</param>
public sealed record DashboardAgentDto(AgentDto Agent, string? LastRunStatus, string? RunningJob, int OpenAlerts);

public sealed record CronPreviewDto(bool Valid, string? Error, IReadOnlyList<DateTimeOffset> Next);

/// <summary>A restic snapshot of the agent's repository, with the Dupli tags parsed.</summary>
public sealed record SnapshotDto(
    string Id,
    string ShortId,
    DateTimeOffset Time,
    string Host,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Tags,
    Guid? PolicyId,
    string? PolicyName,
    string? SourceId,

    /// <summary><c>dir</c> or <c>pg</c> (restic <c>type=</c> tag).</summary>
    string? Type,

    /// <summary>PostgreSQL database of a <c>pg</c> snapshot (<c>_globals</c> for roles/tablespaces).</summary>
    string? Database,

    /// <summary>Connection of the policy source that produced this snapshot, if the policy still exists. UI default only.</summary>
    Guid? ConnectionId);

public sealed record SnapshotNodeDto(string Name, string Path, Dupli.Agent.Core.Backup.SnapshotNodeType Type, long Size, DateTimeOffset? ModifiedAt);

public sealed record CreateRestoreRequest
{
    public required string SnapshotId { get; init; }

    /// <summary>Snapshot paths (files or directories) to restore; empty = everything.</summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>Directory on the VM (new or empty). Null = <c>C:\\DupliRestore\\&lt;job-id&gt;</c>.</summary>
    public string? TargetDirectory { get; init; }

    /// <summary>PostgreSQL snapshot only: also <c>pg_restore</c> into this new database.</summary>
    public string? NewDatabase { get; init; }

    /// <summary>Agent connection to create the new database on. Required when <see cref="NewDatabase"/> is set.</summary>
    public Guid? ConnectionId { get; init; }
}

/// <param name="Bound">False while the invitation has not been used for a first sign-in.</param>
public sealed record OperatorUserDto(
    Guid Id,
    string Email,
    OperatorRole Role,
    string? DisplayName,
    bool Bound,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record InviteOperatorRequest(string Email, OperatorRole Role);

public sealed record UpdateOperatorRequest(OperatorRole Role);
