using System.Net;
using Dupli.Server.Api;
using Dupli.Server.Tests.Infrastructure;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class ConnectionsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static PgConnectionRequest Request(string name = "postgres@localhost:5432") => new()
    {
        Name = name,
        Host = "localhost",
        Port = 5432,
        Username = "postgres",
        PasswordSecret = "pg-main",
    };

    [Fact]
    public async Task Connection_can_be_created_read_updated_and_deleted()
    {
        var agent = await _server.CreateAgentAsync();
        var created = await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/connections", Request())).ReadAsync<PgConnectionDto>();
        Assert.Equal("postgres@localhost:5432", created.Name);
        Assert.Equal(agent.Id, created.AgentId);

        var fetched = await (await _server.Admin().GetAsync($"/api/admin/connections/{created.Id}")).ReadAsync<PgConnectionDto>();
        Assert.Equal(created.Id, fetched.Id);

        var listed = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.Id}/connections")).ReadAsync<List<PgConnectionDto>>();
        Assert.Single(listed);

        var updated = await (await _server.Admin().PutJsonAsync($"/api/admin/connections/{created.Id}", Request() with { Host = "db.internal" }))
            .ReadAsync<PgConnectionDto>();
        Assert.Equal("db.internal", updated.Host);

        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin().DeleteAsync($"/api/admin/connections/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Admin().GetAsync($"/api/admin/connections/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_name_conflicts()
    {
        var agent = await _server.CreateAgentAsync();
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/connections", Request());
        var second = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/connections", Request());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("", "localhost", 5432, "postgres", "pg-main")]
    [InlineData("name", "localhost", 0, "postgres", "pg-main")]
    [InlineData("name", "localhost", 5432, "", "pg-main")]
    [InlineData("name", "localhost", 5432, "postgres", "bad secret name")]
    public async Task Invalid_connection_is_rejected(string name, string host, int port, string username, string passwordSecret)
    {
        var agent = await _server.CreateAgentAsync();
        var response = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/connections", new PgConnectionRequest
        {
            Name = name,
            Host = host,
            Port = port,
            Username = username,
            PasswordSecret = passwordSecret,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Connection_in_use_by_a_policy_cannot_be_deleted()
    {
        var agent = await _server.CreateAgentAsync();
        var connection = await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/connections", Request())).ReadAsync<PgConnectionDto>();
        await _server.Admin().PostJsonAsync($"/api/admin/agents/{agent.Id}/policies", new PolicyRequest
        {
            Name = "nightly",
            Cron = "0 2 * * *",
            Sources = [new PolicyPostgresSourceDto { SourceId = "pg", ConnectionId = connection.Id }],
        });

        var delete = await _server.Admin().DeleteAsync($"/api/admin/connections/{connection.Id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task Policy_needs_a_connection_that_belongs_to_the_agent()
    {
        var agentA = await _server.CreateAgentAsync("vm-a");
        var agentB = await _server.CreateAgentAsync("vm-b");
        var connectionB = await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentB.Id}/connections", Request())).ReadAsync<PgConnectionDto>();

        var response = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentA.Id}/policies", new PolicyRequest
        {
            Name = "nightly",
            Cron = "0 2 * * *",
            Sources = [new PolicyPostgresSourceDto { SourceId = "pg", ConnectionId = connectionB.Id }],
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_rejects_a_connection_from_another_agent()
    {
        var agentA = await _server.EnrollAsync("vm-a", "machine-a");
        var agentB = await _server.CreateAgentAsync("vm-b");
        var connectionB = await (await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentB.Id}/connections", Request())).ReadAsync<PgConnectionDto>();

        var response = await _server.Admin().PostJsonAsync($"/api/admin/agents/{agentA.AgentId}/restores", new CreateRestoreRequest
        {
            SnapshotId = "abcdef12",
            NewDatabase = "copy",
            ConnectionId = connectionB.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
