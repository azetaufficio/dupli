using System.Net;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Jobs;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

/// <summary>
/// <c>POST api/agents/jobs/{jobId}/credentials</c>: just-in-time credentials, scoped by job type, never
/// persisted server-side beyond the escrow and never logged as values.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class JobCredentialsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private async Task<PgConnectionDto> CreateConnectionAsync(Guid agentId, string? password, string name = "postgres@localhost:5432") =>
        await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentId}/connections", new PgConnectionRequest
        {
            Name = name,
            Host = "localhost",
            Port = 5432,
            Username = "postgres",
            Password = password,
        })).ReadAsync<PgConnectionDto>();

    /// <summary>Assigns the (only) pending job to the agent and moves it to Running.</summary>
    private static async Task<string> ToRunningAsync(EnrolledAgent agent)
    {
        var jobId = Assert.Single(await (await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs")).ReadAsync<List<AgentJobDto>>()).JobId;
        await agent.Client.PostJsonAsync($"/api/jobs/{jobId}/started", new JobStartedRequest { StartedAt = DateTimeOffset.UtcNow });
        return jobId;
    }

    private static Task<HttpResponseMessage> CredentialsAsync(EnrolledAgent agent, string jobId) =>
        agent.Client.PostAsync($"/api/agents/jobs/{jobId}/credentials", null);

    [Fact]
    public async Task Backup_job_gets_the_repository_and_the_referenced_postgres_password()
    {
        var agent = await _server.EnrollAsync("vm-backup");
        var connection = await CreateConnectionAsync(agent.AgentId, "s3kr3t-pg");
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/policies", new PolicyRequest
        {
            Name = "nightly", Cron = "0 2 * * *",
            Sources = [new PolicyPostgresSourceDto { SourceId = "pg", ConnectionId = connection.Id }],
        });
        var policy = Assert.Single(await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}/policies")).ReadAsync<List<PolicyDto>>());
        await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null);
        var jobId = await ToRunningAsync(agent);

        var response = await CredentialsAsync(agent, jobId);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var credentials = await response.ReadAsync<JobCredentialsResponse>();

        Assert.Equal("repo-password-vm-backup", credentials.Repository.Password);
        Assert.Equal("AK-vm-backup", credentials.Repository.AccessKeyId);
        Assert.Equal("SK-vm-backup", credentials.Repository.SecretAccessKey);
        Assert.Equal("s3kr3t-pg", Assert.Single(credentials.Postgres).Value);
        Assert.Equal(connection.PasswordSecret, Assert.Single(credentials.Postgres).Key);
    }

    [Fact]
    public async Task A_postgres_secret_never_set_is_simply_omitted()
    {
        var agent = await _server.EnrollAsync("vm-nopass");
        var connection = await CreateConnectionAsync(agent.AgentId, password: null);
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/policies", new PolicyRequest
        {
            Name = "nightly", Cron = "0 2 * * *",
            Sources = [new PolicyPostgresSourceDto { SourceId = "pg", ConnectionId = connection.Id }],
        });
        var policy = Assert.Single(await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}/policies")).ReadAsync<List<PolicyDto>>());
        await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null);
        var jobId = await ToRunningAsync(agent);

        var credentials = await (await CredentialsAsync(agent, jobId)).ReadAsync<JobCredentialsResponse>();

        Assert.Empty(credentials.Postgres);
        Assert.Equal("repo-password-vm-nopass", credentials.Repository.Password);
    }

    [Theory]
    [InlineData(JobType.Retention)]
    [InlineData(JobType.RepositoryCheck)]
    [InlineData(JobType.RestoreTest)]
    public async Task System_jobs_other_than_backup_and_restore_only_need_the_repository(JobType type)
    {
        var agent = await _server.EnrollAsync("vm-" + type.ToString().ToLowerInvariant());
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/jobs", new RunSystemJobRequest(type));
        var jobId = await ToRunningAsync(agent);

        var credentials = await (await CredentialsAsync(agent, jobId)).ReadAsync<JobCredentialsResponse>();

        Assert.Empty(credentials.Postgres);
        Assert.NotEmpty(credentials.Repository.Password);
    }

    [Fact]
    public async Task RestartAgent_jobs_need_no_credentials()
    {
        var agent = await _server.EnrollAsync("vm-restart");
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/jobs", new RunSystemJobRequest(JobType.RestartAgent));
        var jobId = await ToRunningAsync(agent);

        Assert.Equal(HttpStatusCode.BadRequest, (await CredentialsAsync(agent, jobId)).StatusCode);
    }

    [Fact]
    public async Task Restore_job_gets_the_repository_and_only_the_targeted_postgres_password()
    {
        var agent = await _server.EnrollAsync("vm-restore");
        var connection = await CreateConnectionAsync(agent.AgentId, "restore-pw");

        await using var scope = _server.Services.CreateAsyncScope();
        var jobs = _server.Scoped<JobService>(scope);
        var payload = new RestoreJobPayload
        {
            SnapshotId = "abcdef12",
            TargetDirectory = @"C:\Restore",
            Postgres = new PostgresRestoreDto
            {
                Source = new PostgresSourceDto { SourceId = "pg", Username = "postgres", PasswordSecret = connection.PasswordSecret },
                Database = "app",
                NewDatabase = "app_copy",
            },
        };
        await jobs.CreateRestoreJobAsync(agent.AgentId, payload, _server.Time.GetUtcNow(), CancellationToken.None);
        var jobId = await ToRunningAsync(agent);

        var credentials = await (await CredentialsAsync(agent, jobId)).ReadAsync<JobCredentialsResponse>();

        Assert.Equal("restore-pw", Assert.Single(credentials.Postgres).Value);
        Assert.Equal("repo-password-vm-restore", credentials.Repository.Password);
    }

    [Fact]
    public async Task Job_of_another_agent_is_forbidden()
    {
        var owner = await _server.EnrollAsync("vm-owner", "machine-owner");
        var other = await _server.EnrollAsync("vm-other", "machine-other");
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{owner.AgentId}/jobs", new RunSystemJobRequest(JobType.RepositoryCheck));
        var jobId = await ToRunningAsync(owner);

        Assert.Equal(HttpStatusCode.Forbidden, (await CredentialsAsync(other, jobId)).StatusCode);
    }

    [Fact]
    public async Task A_job_not_yet_running_is_a_conflict()
    {
        var agent = await _server.EnrollAsync("vm-notrunning");
        var job = await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/jobs",
            new RunSystemJobRequest(JobType.RepositoryCheck))).ReadAsync<JobDto>();

        // Pending: never even polled yet.
        Assert.Equal(HttpStatusCode.Conflict, (await CredentialsAsync(agent, job.Id.ToString())).StatusCode);

        // Assigned: polled, but the agent has not called /started yet.
        await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs");
        Assert.Equal(HttpStatusCode.Conflict, (await CredentialsAsync(agent, job.Id.ToString())).StatusCode);
    }

    [Fact]
    public async Task The_stored_job_payload_never_carries_a_secret_value()
    {
        var agent = await _server.EnrollAsync("vm-audit");
        var connection = await CreateConnectionAsync(agent.AgentId, "top-secret-password");
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.AgentId}/policies", new PolicyRequest
        {
            Name = "nightly", Cron = "0 2 * * *",
            Sources = [new PolicyPostgresSourceDto { SourceId = "pg", ConnectionId = connection.Id }],
        });
        var policy = Assert.Single(await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}/policies")).ReadAsync<List<PolicyDto>>());
        var job = await (await _server.Admin().PostAsync($"/api/admin/policies/{policy.Id}/run", null)).ReadAsync<JobDto>();

        await using var scope = _server.Services.CreateAsyncScope();
        var db = _server.Scoped<DupliDbContext>(scope);
        var stored = await db.Jobs.FindAsync(job.Id);
        Assert.NotNull(stored);
        Assert.DoesNotContain("top-secret-password", stored!.Payload);
        Assert.DoesNotContain("SK-vm-audit", stored.Payload);
    }
}
