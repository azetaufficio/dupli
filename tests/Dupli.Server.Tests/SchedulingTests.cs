using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Background;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Tests.Infrastructure;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class SchedulingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private async Task<PolicyDto> HourlyPolicyAsync(Guid agentId) =>
        await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentId}/policies", new PolicyRequest
        {
            Name = "hourly",
            Cron = "0 * * * *",
            Sources = [new DirectorySourceDto { SourceId = "docs", Paths = ["/data"] }],
        })).ReadAsync<PolicyDto>();

    private async Task<List<JobDto>> JobsAsync(Guid agentId, JobType? type = null) =>
        (await (await _server.Admin().GetAsync($"/api/admin/jobs?agentId={agentId}")).ReadAsync<List<JobDto>>())
        .Where(j => type is null || j.Type == type).ToList();

    [Fact]
    public async Task Due_occurrences_create_one_job_and_later_ones_coalesce()
    {
        var agent = await _server.EnrollAsync();
        var policy = await HourlyPolicyAsync(agent.AgentId);

        await _server.TickAsync<JobScheduler>();
        Assert.Empty(await JobsAsync(agent.AgentId, JobType.Backup));

        _server.Time.Advance(TimeSpan.FromHours(1));
        await _server.TickAsync<JobScheduler>();
        var job = Assert.Single(await JobsAsync(agent.AgentId, JobType.Backup));
        Assert.Equal(JobTrigger.Schedule, job.Trigger);
        Assert.Equal(0, job.ScheduledAt.Minute);

        _server.Time.Advance(TimeSpan.FromHours(3));
        await _server.TickAsync<JobScheduler>();
        Assert.Single(await JobsAsync(agent.AgentId, JobType.Backup));

        var updated = await (await _server.Admin().GetAsync($"/api/admin/policies/{policy.Id}")).ReadAsync<PolicyDto>();
        Assert.True(updated.LastScheduledFor > job.ScheduledAt);
    }

    [Fact]
    public async Task Offline_agent_misses_the_window_and_alerts_are_deduplicated()
    {
        var agent = await _server.EnrollAsync();
        await HourlyPolicyAsync(agent.AgentId);

        _server.Time.Advance(TimeSpan.FromHours(1));
        await _server.TickAsync<JobScheduler>();
        _server.Time.Advance(TimeSpan.FromHours(12));
        await _server.TickAsync<JobSweeper>();

        var missed = await JobsAsync(agent.AgentId, JobType.Backup);
        Assert.Contains(missed, j => j.State == JobState.Missed);

        await _server.TickAsync<AlertEvaluator>();
        await _server.TickAsync<AlertEvaluator>();

        var alerts = await (await _server.Admin().GetAsync("/api/admin/alerts")).ReadAsync<List<AlertDto>>();
        Assert.Contains(alerts, a => a.Kind == AlertKind.BackupMissed);
        Assert.Contains(alerts, a => a.Kind == AlertKind.AgentOffline);
        Assert.Equal(alerts.Count, _server.Notifications.Sent.Count);

        // Agent comes back: offline alert resolves and a resolution notice is sent once.
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", new Contracts.Agents.HeartbeatRequest { Hostname = "h", Version = "v", OsVersion = "o" });
        await _server.TickAsync<AlertEvaluator>();
        await _server.TickAsync<AlertEvaluator>();

        var open = await (await _server.Admin().GetAsync("/api/admin/alerts")).ReadAsync<List<AlertDto>>();
        Assert.DoesNotContain(open, a => a.Kind == AlertKind.AgentOffline);
        Assert.Single(_server.Notifications.Sent, n => n.Subject.StartsWith("[Dupli] RESOLVED AgentOffline"));
    }

    [Fact]
    public async Task Lapsed_lease_times_out_a_running_job_and_requeues_an_unstarted_one()
    {
        var agent = await _server.EnrollAsync();
        var policy = await HourlyPolicyAsync(agent.AgentId);
        var admin = _server.Admin();

        var job = await (await admin.PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();
        await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs");
        _server.Time.Advance(TimeSpan.FromMinutes(11));
        await _server.TickAsync<JobSweeper>();
        Assert.Equal(JobState.Pending, (await (await admin.GetAsync($"/api/admin/jobs/{job.Id}")).ReadAsync<JobDto>()).State);

        await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs");
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });
        _server.Time.Advance(TimeSpan.FromMinutes(11));
        await _server.TickAsync<JobSweeper>();

        var timedOut = await (await admin.GetAsync($"/api/admin/jobs/{job.Id}")).ReadAsync<JobDto>();
        Assert.Equal(JobState.TimedOut, timedOut.State);

        // The agent learns about it at its next lease renewal.
        var control = await (await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/progress", new JobProgressRequest())).ReadAsync<JobControlResponse>();
        Assert.True(control.CancelRequested);
    }

    [Fact]
    public async Task Maintenance_jobs_are_scheduled_per_agent()
    {
        var agent = await _server.EnrollAsync();
        await HourlyPolicyAsync(agent.AgentId);

        _server.Time.Advance(TimeSpan.FromDays(8));
        await _server.TickAsync<JobScheduler>();
        await _server.TickAsync<JobScheduler>();

        var retention = Assert.Single(await JobsAsync(agent.AgentId, JobType.Retention));
        Assert.Single(await JobsAsync(agent.AgentId, JobType.RepositoryCheck));
        Assert.Equal(JobTrigger.System, retention.Trigger);

        var polled = await (await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs")).ReadAsync<List<AgentJobDto>>();
        var first = Assert.Single(polled);
        if (first.Payload is RetentionJobPayload r)
            Assert.Single(r.Policies);
    }
}
