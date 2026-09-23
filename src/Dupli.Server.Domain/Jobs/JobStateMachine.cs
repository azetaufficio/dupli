namespace Dupli.Server.Domain.Jobs;

/// <summary>
/// Pending → Assigned → Running → Succeeded|Failed|Cancelled|TimedOut.
/// Pending → Missed|Cancelled; Assigned → Pending (lease lost before start)|Missed|Cancelled, or straight
/// to a terminal state when the agent reports a result without a separate "started".
/// Terminal states never change again.
/// </summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        [JobState.Pending] = [JobState.Assigned, JobState.Missed, JobState.Cancelled],
        [JobState.Assigned] = [JobState.Running, JobState.Pending, JobState.Missed, JobState.Cancelled,
                               JobState.Succeeded, JobState.Failed, JobState.TimedOut],
        [JobState.Running] = [JobState.Succeeded, JobState.Failed, JobState.Cancelled, JobState.TimedOut],
    };

    public static IReadOnlyList<JobState> Active { get; } = [JobState.Assigned, JobState.Running];

    public static bool IsTerminal(JobState state) => !Allowed.ContainsKey(state);

    public static bool CanTransition(JobState from, JobState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void Transition(Job job, JobState to, DateTimeOffset now, string? error = null)
    {
        if (!CanTransition(job.State, to))
            throw new InvalidJobTransitionException(job.Id, job.State, to);

        job.State = to;
        switch (to)
        {
            case JobState.Assigned:
                job.AssignedAt = now;
                break;
            case JobState.Running:
                job.StartedAt ??= now;
                break;
            case JobState.Pending:
                job.AssignedAt = null;
                job.LeaseUntil = null;
                break;
        }

        if (IsTerminal(to))
        {
            job.CompletedAt = now;
            job.LeaseUntil = null;
            job.Error = error ?? job.Error;
        }
    }
}

public sealed class InvalidJobTransitionException(Guid jobId, JobState from, JobState to)
    : InvalidOperationException($"Job {jobId} cannot move from {from} to {to}")
{
    public JobState From { get; } = from;
    public JobState To { get; } = to;
}
