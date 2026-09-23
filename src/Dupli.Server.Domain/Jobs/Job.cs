using Dupli.Contracts.Jobs;

namespace Dupli.Server.Domain.Jobs;

public enum JobState
{
    Pending,
    Assigned,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Missed,
}

public enum JobTrigger
{
    Schedule,
    Manual,
    System,
}

public sealed class Job
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid? PolicyId { get; set; }
    public JobType Type { get; set; }
    public JobTrigger Trigger { get; set; }
    public JobState State { get; set; } = JobState.Pending;

    /// <summary>Serialized <c>JobPayloadDto</c> (jsonb). Full spec, never secrets.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }

    /// <summary>A job still Pending at this instant becomes <see cref="JobState.Missed"/>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public bool CancelRequested { get; set; }
    public string? Error { get; set; }

    /// <summary>Serialized <c>JobItemResultDto</c> list (jsonb) from the agent's final report.</summary>
    public string? ResultItems { get; set; }

    public bool IsTerminal => JobStateMachine.IsTerminal(State);
}
