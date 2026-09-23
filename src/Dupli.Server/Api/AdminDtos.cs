using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;

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
    string? ResticUpdateError);

/// <summary>Channel/pins an operator can change from the UI.</summary>
public sealed record UpdateAgentSettingsRequest(string Channel, string? PinnedAgentVersion, string? PinnedResticVersion);

public sealed record EnrollmentTokenDto(string Token, DateTimeOffset ExpiresAt);

public sealed record PolicyRequest
{
    public required string Name { get; init; }
    public required string Cron { get; init; }
    public string TimeZone { get; init; } = "Europe/Rome";
    public bool Enabled { get; init; } = true;
    public RetentionDto Retention { get; init; } = new();
    public required IReadOnlyList<BackupSourceDto> Sources { get; init; }
}

public sealed record PolicyDto(
    Guid Id,
    Guid AgentId,
    string Name,
    string Cron,
    string TimeZone,
    bool Enabled,
    RetentionDto Retention,
    IReadOnlyList<BackupSourceDto> Sources,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastScheduledFor);

public sealed record RunSystemJobRequest(JobType Type);

public sealed record JobDto(
    Guid Id,
    Guid AgentId,
    Guid? PolicyId,
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
    Guid AgentId,
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
    Guid? PolicyId,
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
