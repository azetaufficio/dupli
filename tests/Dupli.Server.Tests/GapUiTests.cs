using System.Net;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Background;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

/// <summary>Section 5 of the gap-UI plan: run-all policies, agent deletion (with cascade), keyset paging and
/// AgentName/PolicyName in the list DTOs.</summary>
[Collection(ServerCollection.Name)]
public sealed class GapUiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static PolicyRequest DirPolicy(string name, bool enabled = true) => new()
    {
        Name = name,
        Cron = "0 2 * * *",
        Enabled = enabled,
        Sources = [new PolicyDirectorySourceDto { SourceId = "docs", Paths = [@"D:\Docs"] }],
    };

    private async Task<PolicyDto> CreatePolicyAsync(Guid agentId, string name, bool enabled = true) =>
        await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentId}/policies", DirPolicy(name, enabled)))
            .ReadAsync<PolicyDto>();

    [Fact]
    public async Task Run_all_creates_a_job_per_enabled_policy_and_skips_disabled_ones()
    {
        var agent = await _server.CreateAgentAsync();
        await CreatePolicyAsync(agent.Id, "p1");
        await CreatePolicyAsync(agent.Id, "p2");
        await CreatePolicyAsync(agent.Id, "p3", enabled: false);

        var response = await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/policies/run", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jobs = await response.ReadAsync<List<JobDto>>();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(JobType.Backup, j.Type));

        // Already-pending policies are skipped, not an error for the whole batch.
        var second = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/policies/run", null)).ReadAsync<List<JobDto>>();
        Assert.Empty(second);
    }

    [Fact]
    public async Task Delete_agent_conflicts_while_active_or_with_an_active_job()
    {
        var agent = await _server.EnrollAsync(); // Active once enrolled.
        Assert.Equal(HttpStatusCode.Conflict, (await _server.Admin().DeleteAsync($"/api/admin/agents/{agent.AgentId}")).StatusCode);

        await _server.Admin().PostAsync($"/api/admin/agents/{agent.AgentId}/disable", null);
        var policy = await CreatePolicyAsync(agent.AgentId, "p1");
        await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null);
        Assert.Equal(HttpStatusCode.Conflict, (await _server.Admin().DeleteAsync($"/api/admin/agents/{agent.AgentId}")).StatusCode);
    }

    [Fact]
    public async Task Delete_agent_cascades_its_data()
    {
        await _server.SeedOperatorUserAsync();
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId, "p1");
        var connection = await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/connections", new PgConnectionRequest
        {
            Name = "pg", Host = "localhost", Port = 5432, Username = "postgres", PasswordSecret = "pg-main",
        })).ReadAsync<PgConnectionDto>();

        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();
        var polled = await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs");
        Assert.Equal(HttpStatusCode.OK, polled.StatusCode);
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/failed", new JobResultDto
        {
            Outcome = JobOutcome.Interrupted,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
        });
        await _server.TickAsync<AlertEvaluator>();
        Assert.NotEmpty(await (await _server.Admin().GetAsync("/api/admin/alerts")).ReadAsync<List<AlertDto>>());

        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = _server.Scoped<DupliDbContext>(scope);
            db.Logs.Add(new AgentLog { AgentId = agent.AgentId, Timestamp = DateTimeOffset.UtcNow, Level = "Information", Message = "hello" });
            await db.SaveChangesAsync();
        }

        await _server.Admin().PostAsync($"/api/admin/agents/{agent.AgentId}/disable", null);
        var delete = await _server.Admin().DeleteAsync($"/api/admin/agents/{agent.AgentId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).StatusCode);

        await using var verifyScope = _server.Services.CreateAsyncScope();
        var verifyDb = _server.Scoped<DupliDbContext>(verifyScope);
        Assert.False(await verifyDb.Policies.AnyAsync(p => p.Id == policy.Id));
        Assert.False(await verifyDb.PgConnections.AnyAsync(c => c.Id == connection.Id));
        Assert.False(await verifyDb.Jobs.AnyAsync(j => j.AgentId == agent.AgentId));
        Assert.False(await verifyDb.Runs.AnyAsync(r => r.AgentId == agent.AgentId));
        Assert.False(await verifyDb.Logs.AnyAsync(l => l.AgentId == agent.AgentId));
        Assert.False(await verifyDb.Alerts.AnyAsync(a => a.AgentId == agent.AgentId));
        Assert.False(await verifyDb.Notifications.AnyAsync(n => n.AgentId == agent.AgentId));
    }

    [Fact]
    public async Task Job_paging_is_stable_across_a_batch_created_with_the_same_timestamp()
    {
        var agent = await _server.CreateAgentAsync();
        for (var i = 0; i < 5; i++)
            await CreatePolicyAsync(agent.Id, $"p{i}");

        // Run-all creates all 5 jobs in the same request: same CreatedAt, distinct ids. Paging with a small
        // limit must still see each job exactly once via the "createdAt, id" keyset.
        var created = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/policies/run", null)).ReadAsync<List<JobDto>>();
        Assert.Equal(5, created.Count);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/admin/jobs?agentId={agent.Id}&limit=2" + (cursor is null ? "" : $"&before={Uri.EscapeDataString(cursor)}");
            var page = await (await _server.Admin().GetAsync(url)).ReadAsync<PagedDto<JobDto>>();
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(j => j.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(created.Select(j => j.Id).OrderBy(id => id), seen.OrderBy(id => id));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Log_paging_is_stable_across_entries_with_the_same_timestamp()
    {
        var agent = await _server.CreateAgentAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = _server.Scoped<DupliDbContext>(scope);
            for (var i = 0; i < 5; i++)
                db.Logs.Add(new AgentLog { AgentId = agent.Id, Timestamp = timestamp, Level = "Information", Message = $"m{i}" });
            await db.SaveChangesAsync();
        }

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var url = $"/api/admin/logs?agentId={agent.Id}&limit=2" + (cursor is null ? "" : $"&before={Uri.EscapeDataString(cursor)}");
            var page = await (await _server.Admin().GetAsync(url)).ReadAsync<PagedDto<LogDto>>();
            Assert.True(page.Items.Count <= 2);
            Assert.All(page.Items, l => Assert.Equal(agent.Name, l.AgentName));
            seen.AddRange(page.Items.Select(l => l.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task Run_paging_is_stable_across_runs_with_the_same_completed_at()
    {
        var agent = await _server.EnrollAsync();
        var completedAt = DateTimeOffset.UtcNow;
        var runIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var policy = await CreatePolicyAsync(agent.AgentId, $"p{i}");
            var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();
            await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs");
            await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = completedAt.AddMinutes(-1) });
            await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/completed", new JobResultDto
            {
                Outcome = JobOutcome.Succeeded,
                StartedAt = completedAt.AddMinutes(-1),
                CompletedAt = completedAt,
                Items = [new JobItemResultDto { SourceId = "docs", Item = @"D:\Docs", Outcome = JobOutcome.Succeeded, SnapshotId = $"s{i}" }],
            });
            runIds.Add(job.Id.ToString());
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/admin/runs?agentId={agent.AgentId}&limit=1" + (cursor is null ? "" : $"&before={Uri.EscapeDataString(cursor)}");
            var page = await (await _server.Admin().GetAsync(url)).ReadAsync<PagedDto<RunDto>>();
            Assert.True(page.Items.Count <= 1);
            Assert.All(page.Items, r => Assert.Equal(agent.AgentId.ToString(), r.AgentId.ToString()));
            Assert.All(page.Items, r => Assert.NotNull(r.PolicyName));
            seen.AddRange(page.Items.Select(r => r.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task Alert_dto_carries_agent_and_policy_names()
    {
        await _server.SeedOperatorUserAsync();
        var agent = await _server.EnrollAsync();
        var policy = await CreatePolicyAsync(agent.AgentId, "p1");
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();
        await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs");
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });
        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/failed", new JobResultDto
        {
            Outcome = JobOutcome.Failed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Error = "boom",
        });
        await _server.TickAsync<AlertEvaluator>();

        var alert = Assert.Single(await (await _server.Admin().GetAsync("/api/admin/alerts")).ReadAsync<List<AlertDto>>());
        Assert.Equal("vm-01", alert.AgentName);
        Assert.Equal("p1", alert.PolicyName);
    }
}
