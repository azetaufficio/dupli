using System.Text.Json.Serialization;
using Dupli.Contracts.Policies;

namespace Dupli.Contracts.Jobs;

/// <summary>A job handed to an agent. Typed payload only: never a command line.</summary>
public sealed record AgentJobDto
{
    public required string JobId { get; init; }
    public required JobType Type { get; init; }
    public required JobPayloadDto Payload { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }

    /// <summary>The agent must renew the lease (started/progress) before this instant.</summary>
    public required DateTimeOffset LeaseUntil { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BackupJobPayload), "backup")]
[JsonDerivedType(typeof(RetentionJobPayload), "retention")]
[JsonDerivedType(typeof(RepositoryCheckJobPayload), "repositoryCheck")]
[JsonDerivedType(typeof(RestoreTestJobPayload), "restoreTest")]
public abstract record JobPayloadDto;

public sealed record BackupJobPayload : JobPayloadDto
{
    public required PolicySpecDto Policy { get; init; }
}

/// <summary>forget per policy (by <c>policy=</c> tag), then a single prune.</summary>
public sealed record RetentionJobPayload : JobPayloadDto
{
    public required IReadOnlyList<PolicyRetentionDto> Policies { get; init; }
    public bool Prune { get; init; } = true;
}

public sealed record PolicyRetentionDto
{
    public required string PolicyId { get; init; }
    public required RetentionDto Retention { get; init; }
}

public sealed record RepositoryCheckJobPayload : JobPayloadDto
{
    public int ReadDataSubsetPercent { get; init; } = 5;
}

/// <summary>Restores a sample into a temporary directory and verifies it (executor lands in M3).</summary>
public sealed record RestoreTestJobPayload : JobPayloadDto
{
    public int SampleFiles { get; init; } = 20;
}

public sealed record JobStartedRequest
{
    public required DateTimeOffset StartedAt { get; init; }
}

public sealed record JobProgressRequest
{
    public string? Message { get; init; }
}

/// <summary>Returned by started/progress: renewed lease and whether the operator asked to cancel.</summary>
public sealed record JobControlResponse
{
    public required DateTimeOffset LeaseUntil { get; init; }
    public bool CancelRequested { get; init; }
}

public enum JobOutcome
{
    Succeeded,
    SucceededWithWarnings,
    Failed,
    Cancelled,

    /// <summary>The agent restarted while the job was running.</summary>
    Interrupted,
}

public sealed record JobResultDto
{
    public required JobOutcome Outcome { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public IReadOnlyList<JobItemResultDto> Items { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>"Transient" or "Permanent" when <see cref="Error"/> is set.</summary>
    public string? ErrorKind { get; init; }
}

public sealed record JobItemResultDto
{
    public required string SourceId { get; init; }
    public required string Item { get; init; }
    public required JobOutcome Outcome { get; init; }
    public string? SnapshotId { get; init; }
    public long BytesProcessed { get; init; }
    public long BytesAdded { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
}
