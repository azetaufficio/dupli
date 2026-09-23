using Dupli.Server.Domain.Jobs;

namespace Dupli.Server.Tests;

public sealed class JobStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);

    private static Job NewJob() => new() { Id = Guid.NewGuid(), Payload = "{}" };

    [Fact]
    public void Happy_path_sets_timestamps_and_clears_lease()
    {
        var job = NewJob();
        JobStateMachine.Transition(job, JobState.Assigned, Now);
        job.LeaseUntil = Now.AddMinutes(10);
        JobStateMachine.Transition(job, JobState.Running, Now.AddSeconds(5));
        JobStateMachine.Transition(job, JobState.Succeeded, Now.AddMinutes(3));

        Assert.Equal(Now, job.AssignedAt);
        Assert.Equal(Now.AddSeconds(5), job.StartedAt);
        Assert.Equal(Now.AddMinutes(3), job.CompletedAt);
        Assert.Null(job.LeaseUntil);
        Assert.True(job.IsTerminal);
    }

    [Theory]
    [InlineData(JobState.Succeeded)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.TimedOut)]
    [InlineData(JobState.Missed)]
    public void Terminal_states_never_change(JobState terminal)
    {
        foreach (var to in Enum.GetValues<JobState>())
            Assert.False(JobStateMachine.CanTransition(terminal, to));
    }

    [Fact]
    public void Pending_cannot_jump_to_running_or_succeeded()
    {
        var job = NewJob();
        Assert.Throws<InvalidJobTransitionException>(() => JobStateMachine.Transition(job, JobState.Running, Now));
        Assert.Throws<InvalidJobTransitionException>(() => JobStateMachine.Transition(job, JobState.Succeeded, Now));
    }

    [Fact]
    public void Assigned_can_return_to_pending_and_loses_assignment()
    {
        var job = NewJob();
        JobStateMachine.Transition(job, JobState.Assigned, Now);
        job.LeaseUntil = Now.AddMinutes(10);
        JobStateMachine.Transition(job, JobState.Pending, Now.AddMinutes(11));

        Assert.Null(job.AssignedAt);
        Assert.Null(job.LeaseUntil);
        Assert.Equal(JobState.Pending, job.State);
    }
}
