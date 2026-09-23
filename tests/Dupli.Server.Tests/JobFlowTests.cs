using System.Net;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Background;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Tests.Infrastructure;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class JobFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static PolicyRequest Policy(string cron = "0 2 * * *") => new()
    {
        Name = "nightly",
        Cron = cron,
        TimeZone = "Europe/Rome",
        Retention = new RetentionDto { KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 6 },
        Sources =
        [
            new DirectorySourceDto { SourceId = "docs", Paths = [@"D:\Docs"], Excludes = ["*.tmp"] },
            new PostgresSourceDto { SourceId = "pg", Username = "postgres", PasswordSecret = "pg-main", ExcludeDatabases = ["scratch"] },
        ],
    };

    private async Task<PolicyDto> CreatePolicyAsync(Guid agentId, PolicyRequest? request = null) =>
        await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentId}/policies", request ?? Policy())).ReadAsync<PolicyDto>();

    private static async Task<IReadOnlyList<AgentJobDto>> PollAsync(EnrolledAgent agent) =>
        await (await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs")).ReadAsync<List<AgentJobDto>>();

    private static JobResultDto Result(JobOutcome outcome, string? error = null) => new()
    {
        Outcome = outcome,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        CompletedAt = DateTimeOffset.UtcNow,
        Error = error,
        Items =
        [
            new JobItemResultDto { SourceId = "docs", Item = @"D:\Docs", Outcome = outcome, SnapshotId = "abc123", BytesProcessed = 1000, BytesAdded = 100 },
        ],
    };

    [Fact]
    public async Task Run_now_is_assigned_once_executed_and_recorded()
    {
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId);
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();

        var polled = Assert.Single(await PollAsync(agent));
        Assert.Equal(job.Id.ToString(), polled.JobId);
        var payload = Assert.IsType<BackupJobPayload>(polled.Payload);
        Assert.Equal(policy.Id.ToString(), payload.Policy.PolicyId);
        Assert.Equal(2, payload.Policy.Sources.Count);
        var pg = Assert.IsType<PostgresSourceDto>(payload.Policy.Sources.Single(s => s.SourceId == "pg"));
        Assert.Equal("pg-main", pg.PasswordSecret);
        Assert.Equal(["scratch"], pg.ExcludeDatabases);

        // Lost response: the same job is delivered again, nothing new is assigned.
        Assert.Equal(polled.JobId, Assert.Single(await PollAsync(agent)).JobId);

        var control = await (await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow }))
            .ReadAsync<JobControlResponse>();
        Assert.False(control.CancelRequested);
        Assert.Empty(await PollAsync(agent));

        Assert.Equal(HttpStatusCode.NoContent, (await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/completed", Result(JobOutcome.Succeeded))).StatusCode);
        // Idempotent re-report (agent outbox retry).
        Assert.Equal(HttpStatusCode.NoContent, (await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/completed", Result(JobOutcome.Succeeded))).StatusCode);

        var finished = await (await _server.Admin().GetAsync($"/api/admin/jobs/{job.Id}")).ReadAsync<JobDto>();
        Assert.Equal(JobState.Succeeded, finished.State);

        var run = Assert.Single(await (await _server.Admin().GetAsync($"/api/admin/runs?policyId={policy.Id}")).ReadAsync<List<RunDto>>());
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal("abc123", Assert.Single(run.Items).SnapshotId);

        var details = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.NotNull(details.LastBackupAt);
    }

    [Fact]
    public async Task Pending_backup_is_coalesced()
    {
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId);

        Assert.Equal(HttpStatusCode.Accepted, (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).StatusCode);
    }

    [Fact]
    public async Task One_active_job_per_agent()
    {
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId);
        await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null);
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/jobs", new RunSystemJobRequest(JobType.RepositoryCheck));

        var first = Assert.Single(await PollAsync(agent));
        await agent.Client.PostJsonAsync($"/api/jobs/{first.JobId}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });
        Assert.Empty(await PollAsync(agent));

        await agent.Client.PostJsonAsync($"/api/jobs/{first.JobId}/completed", Result(JobOutcome.Succeeded));
        var second = Assert.Single(await PollAsync(agent));
        Assert.NotEqual(first.JobId, second.JobId);
    }

    [Fact]
    public async Task Agent_cannot_poll_or_report_for_another_agent()
    {
        var a = await _server.EnrollAsync("vm-a", "machine-a");
        var b = await _server.EnrollAsync("vm-b", "machine-b");
        var policy = await CreatePolicyAsync(a.AgentId);
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();

        Assert.Equal(HttpStatusCode.Forbidden, (await b.Client.GetAsync($"/api/agents/{a.AgentId}/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostJsonAsync($"/api/jobs/{job.Id}/completed", Result(JobOutcome.Succeeded))).StatusCode);
    }

    [Fact]
    public async Task Cancel_of_a_running_job_reaches_the_agent()
    {
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId);
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();
        await PollAsync(agent);
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });

        var cancelled = await (await _server.Admin().PostAsync($"/api/admin/jobs/{job.Id}/cancel", null)).ReadAsync<JobDto>();
        Assert.Equal(JobState.Running, cancelled.State);
        Assert.True(cancelled.CancelRequested);

        var control = await (await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/progress", new JobProgressRequest())).ReadAsync<JobControlResponse>();
        Assert.True(control.CancelRequested);

        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/failed", Result(JobOutcome.Cancelled, "cancelled"));
        Assert.Equal(JobState.Cancelled, (await (await _server.Admin().GetAsync($"/api/admin/jobs/{job.Id}")).ReadAsync<JobDto>()).State);
    }

    [Fact]
    public async Task Cancel_of_a_pending_job_is_immediate()
    {
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId);
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();

        var cancelled = await (await _server.Admin().PostAsync($"/api/admin/jobs/{job.Id}/cancel", null)).ReadAsync<JobDto>();
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Empty(await PollAsync(agent));
    }

    [Fact]
    public async Task Interrupted_report_fails_the_job_and_raises_a_single_alert()
    {
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId);
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();
        await PollAsync(agent);
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", new Contracts.Agents.HeartbeatRequest { Hostname = "h", Version = "v", OsVersion = "o" });

        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/failed", Result(JobOutcome.Interrupted));
        var failed = await (await _server.Admin().GetAsync($"/api/admin/jobs/{job.Id}")).ReadAsync<JobDto>();
        Assert.Equal(JobState.Failed, failed.State);
        Assert.StartsWith("Interrupted", failed.Error);

        await _server.TickAsync<AlertEvaluator>();
        await _server.TickAsync<AlertEvaluator>();

        var alert = Assert.Single(await (await _server.Admin().GetAsync("/api/admin/alerts")).ReadAsync<List<AlertDto>>());
        Assert.Equal(AlertKind.BackupFailed, alert.Kind);
        Assert.Single(_server.Notifications.Sent);
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("61 * * * *")]
    public async Task Invalid_policy_is_rejected(string cron)
    {
        var agent = await _server.CreateAgentAsync();
        var response = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/policies", Policy(cron));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Policy_round_trips_polymorphic_sources()
    {
        var agent = await _server.CreateAgentAsync();
        var created = await CreatePolicyAsync(agent.Id);
        var loaded = await (await _server.Admin().GetAsync($"/api/admin/policies/{created.Id}")).ReadAsync<PolicyDto>();

        var dir = Assert.IsType<DirectorySourceDto>(loaded.Sources.Single(s => s.SourceId == "docs"));
        Assert.Equal([@"D:\Docs"], dir.Paths);
        Assert.Equal(["*.tmp"], dir.Excludes);
        Assert.IsType<PostgresSourceDto>(loaded.Sources.Single(s => s.SourceId == "pg"));
        Assert.NotNull(loaded.NextRunAt);
    }

    [Fact]
    public async Task Only_database_selection_round_trips_and_needs_a_list()
    {
        var agent = await _server.CreateAgentAsync();
        PolicyRequest WithSelection(params string[] databases) => Policy() with
        {
            Sources =
            [
                new PostgresSourceDto
                {
                    SourceId = "pg", Username = "postgres", PasswordSecret = "pg-main",
                    DatabaseSelection = DatabaseSelection.Only, IncludeDatabases = databases,
                },
            ],
        };

        var empty = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/policies", WithSelection());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var created = await CreatePolicyAsync(agent.Id, WithSelection("postgres", "erp"));
        var pg = Assert.IsType<PostgresSourceDto>(Assert.Single(created.Sources));
        Assert.Equal(DatabaseSelection.Only, pg.DatabaseSelection);
        Assert.Equal(["postgres", "erp"], pg.IncludeDatabases);
    }

    [Theory]
    [InlineData("not-hex", null, null)]
    [InlineData("abcdef12", "relative/path", null)]
    [InlineData("abcdef12", "/C/Data/../Windows", null)]
    [InlineData("abcdef12", null, "relative\\dir")]
    public async Task Invalid_restore_request_is_rejected_before_reading_the_repository(
        string snapshotId, string? include, string? target)
    {
        var agent = await _server.CreateAgentAsync();
        var response = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/restores", new CreateRestoreRequest
        {
            SnapshotId = snapshotId,
            Includes = include is null ? [] : [include],
            TargetDirectory = target,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
