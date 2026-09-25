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
[JsonDerivedType(typeof(RestartAgentJobPayload), "restartAgent")]
[JsonDerivedType(typeof(RestoreJobPayload), "restore")]
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

/// <summary>
/// Proves the backups can be read back: for each directory source, a sample of files from the latest
/// snapshot is restored into a temporary directory and verified; for each PostgreSQL source, the latest
/// dump of every database goes through <c>pg_restore --list</c>. Everything is deleted afterwards.
/// </summary>
public sealed record RestoreTestJobPayload : JobPayloadDto
{
    /// <summary>Policies whose snapshots are tested (by <c>policy=</c>/<c>source=</c> tags).</summary>
    public IReadOnlyList<PolicySpecDto> Policies { get; init; } = [];

    /// <summary>Files sampled per directory source.</summary>
    public int SampleFiles { get; init; } = 20;
}

/// <summary>
/// Restores a snapshot (all of it, or <see cref="Includes"/>) into an alternative directory on the VM, never over a
/// backed-up path. For a PostgreSQL dump, optionally <c>pg_restore</c> into a database that must not exist yet.
/// </summary>
public sealed record RestoreJobPayload : JobPayloadDto
{
    public required string SnapshotId { get; init; }

    /// <summary>Restic paths to restore (files or directories); empty = the whole snapshot.</summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>Must not exist or be empty. Null = default location (<c>C:\DupliRestore\&lt;job-id&gt;</c>).</summary>
    public string? TargetDirectory { get; init; }

    public PostgresRestoreDto? Postgres { get; init; }

    /// <summary>The agent's policies: the target may not overlap any of their paths.</summary>
    public IReadOnlyList<PolicySpecDto> Policies { get; init; } = [];
}

/// <summary>After the files restore, create <see cref="NewDatabase"/> and <c>pg_restore</c> the dump of <see cref="Database"/> into it.</summary>
public sealed record PostgresRestoreDto
{
    /// <summary>Connection of the policy source that produced the dump.</summary>
    public required PostgresSourceDto Source { get; init; }

    /// <summary>Database of the dump (<c>db=</c> tag): the file restored is <c>&lt;Database&gt;.dump</c>.</summary>
    public required string Database { get; init; }

    public required string NewDatabase { get; init; }
}

/// <summary>The agent reports success, then exits so the service manager restarts it.</summary>
public sealed record RestartAgentJobPayload : JobPayloadDto;

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

    /// <summary>Where the item ended up (restore: target directory or database).</summary>
    public string? Location { get; init; }
}

/// <summary>
/// Answer to <c>POST api/agents/jobs/{jobId}/credentials</c>: just-in-time credentials for one Running job,
/// scoped to what that job type actually needs (see <c>JobCredentialsService</c> server-side). The agent keeps
/// this only in memory for the duration of the job and never writes it to disk.
/// </summary>
public sealed class JobCredentialsResponse
{
    public required JobRepositoryCredentialsDto Repository { get; init; }

    /// <summary>PostgreSQL passwords keyed by <c>PasswordSecret</c> name. A source whose secret is not set on
    /// the server is simply omitted: only that source fails, like today.</summary>
    public IReadOnlyDictionary<string, string> Postgres { get; init; } = new Dictionary<string, string>();

    public override string ToString() => "JobCredentialsResponse";
}

/// <summary>The restic repository's password and backend (S3) credentials for one job.</summary>
public sealed class JobRepositoryCredentialsDto
{
    public required string Password { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public string? SessionToken { get; init; }

    public override string ToString() => "JobRepositoryCredentialsDto";
}
