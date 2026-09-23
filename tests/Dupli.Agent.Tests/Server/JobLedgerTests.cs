using Dupli.Agent.Server;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;

namespace Dupli.Agent.Tests.Server;

public sealed class JobLedgerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();

    private JobLedger NewLedger() => new(_dir.Combine("agent.db"));

    private static JobResultDto Result(JobOutcome outcome) =>
        new() { Outcome = outcome, StartedAt = Now, CompletedAt = Now.AddMinutes(5) };

    [Fact]
    public void A_job_is_begun_at_most_once_across_restarts()
    {
        var ledger = NewLedger();
        Assert.True(ledger.TryBegin("job-1", JobType.Backup, Now));
        ledger.MarkRunning("job-1", Now);

        Assert.False(NewLedger().TryBegin("job-1", JobType.Backup, Now));
    }

    [Fact]
    public void Received_but_never_started_job_may_still_run()
    {
        NewLedger().TryBegin("job-1", JobType.Backup, Now);
        Assert.True(NewLedger().TryBegin("job-1", JobType.Backup, Now));
    }

    [Fact]
    public void Finished_results_stay_in_the_outbox_until_reported()
    {
        var ledger = NewLedger();
        ledger.TryBegin("job-1", JobType.Backup, Now);
        ledger.MarkRunning("job-1", Now);
        ledger.MarkFinished("job-1", Result(JobOutcome.Succeeded));

        var pending = Assert.Single(NewLedger().PendingReports());
        Assert.Equal("job-1", pending.JobId);
        Assert.Equal(JobOutcome.Succeeded, pending.Result.Outcome);
        Assert.Equal(Now.AddMinutes(5), NewLedger().LastSuccessfulBackupAt());

        ledger.MarkReported("job-1");
        Assert.Empty(ledger.PendingReports());
        Assert.Equal(LedgerState.Reported, ledger.GetState("job-1"));
    }

    [Fact]
    public void Running_jobs_of_a_previous_process_are_reported_as_interrupted()
    {
        var ledger = NewLedger();
        ledger.TryBegin("job-1", JobType.Backup, Now);
        ledger.MarkRunning("job-1", Now.AddSeconds(1));

        var (jobId, startedAt) = Assert.Single(NewLedger().Interrupted());
        Assert.Equal("job-1", jobId);
        Assert.Equal(Now.AddSeconds(1), startedAt);
        Assert.Equal(["job-1"], ledger.RunningJobIds());
    }

    [Fact]
    public void Policy_specs_are_kept_for_the_restore_guard()
    {
        var ledger = NewLedger();
        var policy = new PolicySpecDto
        {
            PolicyId = "p1",
            Name = "nightly",
            Sources = [new DirectorySourceDto { SourceId = "docs", Paths = ["/data/docs"] }],
        };
        ledger.SavePolicy(policy, Now);
        ledger.SavePolicy(policy with { Name = "renamed" }, Now);

        var known = Assert.Single(NewLedger().KnownPolicies());
        Assert.Equal("renamed", known.Name);
        Assert.Equal(["/data/docs"], Assert.IsType<DirectorySourceDto>(Assert.Single(known.Sources)).Paths);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }
}
