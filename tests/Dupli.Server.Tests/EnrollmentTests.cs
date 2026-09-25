using System.Net;
using Dupli.Contracts.Agents;
using Dupli.Contracts.Tools;
using Dupli.Server.Api;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class EnrollmentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    [Fact]
    public async Task Registration_delivers_escrowed_credentials_and_pinned_restic_from_the_mirror()
    {
        var agent = await _server.EnrollAsync("vm-01");
        var r = agent.Registration;

        // No business secret in the response: only the agent's identity (AgentId/AgentSecret) and non-secret
        // repository data. Repository password and S3 keys stay server-side; the agent fetches them per job.
        Assert.Equal(new RepositoryDto { Endpoint = "http://localhost:9000", Bucket = "backups", Prefix = "agents/vm-01", Region = "us-east-1" }, r.Repository);
        Assert.Equal("0.19.1", r.ResticManifest.Version);
        Assert.Equal("https://dupli.test/api/tools/restic/0.19.1/windows_amd64", r.ResticManifest.DownloadUrl);

        var details = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.Equal(Domain.Agents.AgentStatus.Active, details.Status);
        Assert.Equal("vm-01.local", details.Hostname);
    }

    [Fact]
    public async Task Registration_response_body_never_contains_the_repository_password_or_the_S3_secret_key()
    {
        var agent = await _server.CreateAgentAsync("vm-secretcheck"); // repo-password-vm-secretcheck / SK-vm-secretcheck
        var token = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/enrollment-tokens", null)).ReadAsync<EnrollmentTokenDto>();

        var response = await _server.CreateClient().PostJsonAsync("/api/agents/register", new RegisterAgentRequest
        {
            EnrollmentToken = token.Token, MachineId = "m", Hostname = "h", OsVersion = "os", AgentVersion = "0.1.0",
        });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("repo-password-vm-secretcheck", raw);
        Assert.DoesNotContain("SK-vm-secretcheck", raw);
    }

    [Theory]
    [InlineData("linux_arm64", HttpStatusCode.OK)]
    [InlineData("linux_amd64", HttpStatusCode.OK)]
    [InlineData("plan9_mips", HttpStatusCode.BadRequest)]
    public async Task Registration_returns_the_restic_build_of_the_agent_platform(string platform, HttpStatusCode expected)
    {
        var agent = await _server.CreateAgentAsync();
        var token = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/enrollment-tokens", null)).ReadAsync<EnrollmentTokenDto>();

        var response = await _server.CreateClient().PostJsonAsync("/api/agents/register", new RegisterAgentRequest
        {
            EnrollmentToken = token.Token, MachineId = "m", Hostname = "h", OsVersion = "Linux", AgentVersion = "0.1.0", Platform = platform,
        });

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
            Assert.EndsWith($"/api/tools/restic/0.19.1/{platform}", (await response.ReadAsync<RegisterAgentResponse>()).ResticManifest.DownloadUrl);
    }

    [Fact]
    public async Task Enrollment_token_is_single_use()
    {
        var agent = await _server.CreateAgentAsync();
        var token = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/enrollment-tokens", null)).ReadAsync<EnrollmentTokenDto>();
        var request = new RegisterAgentRequest
        {
            EnrollmentToken = token.Token, MachineId = "m", Hostname = "h", OsVersion = "os", AgentVersion = "0.1.0",
        };

        var client = _server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/agents/register", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/agents/register", request)).StatusCode);
    }

    [Fact]
    public async Task Expired_enrollment_token_is_rejected()
    {
        var agent = await _server.CreateAgentAsync();
        var token = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/enrollment-tokens", null)).ReadAsync<EnrollmentTokenDto>();
        _server.Time.Advance(TimeSpan.FromHours(25));

        var response = await _server.CreateClient().PostJsonAsync("/api/agents/register", new RegisterAgentRequest
        {
            EnrollmentToken = token.Token, MachineId = "m", Hostname = "h", OsVersion = "os", AgentVersion = "0.1.0",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reenrollment_is_bound_to_the_original_machine()
    {
        var agent = await _server.EnrollAsync("vm-01", machineId: "machine-A");
        var token = await (await _server.Admin().PostAsync($"/api/admin/agents/{agent.AgentId}/enrollment-tokens", null)).ReadAsync<EnrollmentTokenDto>();

        var response = await _server.CreateClient().PostJsonAsync("/api/agents/register", new RegisterAgentRequest
        {
            EnrollmentToken = token.Token, MachineId = "machine-B", Hostname = "other", OsVersion = "os", AgentVersion = "0.1.0",
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_secret_gets_no_token_and_rotation_invalidates_the_old_secret()
    {
        var agent = await _server.EnrollAsync();
        var anonymous = _server.CreateClient();

        var wrong = await anonymous.PostJsonAsync("/api/agents/token", new AgentTokenRequest { AgentId = agent.Registration.AgentId, AgentSecret = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var rotated = await (await agent.Client.PostAsync("/api/agents/secret/rotate", null)).ReadAsync<RotateSecretResponse>();
        var old = await anonymous.PostJsonAsync("/api/agents/token", new AgentTokenRequest { AgentId = agent.Registration.AgentId, AgentSecret = agent.Registration.AgentSecret });
        var fresh = await anonymous.PostJsonAsync("/api/agents/token", new AgentTokenRequest { AgentId = agent.Registration.AgentId, AgentSecret = rotated.AgentSecret });

        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_updates_agent_and_requires_a_token()
    {
        var agent = await _server.EnrollAsync();
        var heartbeat = new HeartbeatRequest
        {
            Hostname = "vm-01.renamed", Version = "0.1.1", ResticVersion = "0.19.1", OsVersion = "Windows Server 2025", FreeDiskSpace = 42,
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await _server.CreateClient().PostJsonAsync("/api/agents/heartbeat", heartbeat)).StatusCode);
        var response = await (await agent.Client.PostJsonAsync("/api/agents/heartbeat", heartbeat)).ReadAsync<HeartbeatResponse>();
        Assert.Equal(30, response.PollIntervalSeconds);

        var details = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.True(details.Online);
        Assert.Equal("vm-01.renamed", details.Hostname);
        Assert.Equal("0.19.1", details.ResticVersion);
        Assert.Equal(42, details.FreeDiskSpace);
    }

    [Fact]
    public async Task Mirror_serves_cached_assets_with_a_relative_mirror_path()
    {
        // Relative ToolMirrorPath (as in appsettings.Development.json): must not be resolved against the web root.
        var relative = Path.Combine("mirror-tests", Guid.NewGuid().ToString("N"));
        var content = "fake restic archive"u8.ToArray();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var cached = Path.Combine(relative, "restic", "9.9.9");
        Directory.CreateDirectory(cached);
        await File.WriteAllBytesAsync(Path.Combine(cached, "restic_9.9.9_linux_amd64.bz2"), content);
        try
        {
            using var server = _server.WithWebHostBuilder(b => b.UseSetting("Dupli:ToolMirrorPath", relative));
            var admin = server.CreateClient();
            admin.DefaultRequestHeaders.Add(Auth.AuthConstants.AdminKeyHeader, DupliTestServer.AdminKey);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostJsonAsync("/api/admin/releases/restic", new CreateReleaseRequest(
                "9.9.9", "linux_amd64", "https://example.com/restic_9.9.9_linux_amd64.bz2", sha, MakeCurrent: false))).StatusCode);

            var response = await server.CreateClient().GetAsync("/api/tools/restic/9.9.9/linux_amd64");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            Directory.Delete(relative, recursive: true);
        }
    }

    [Fact]
    public async Task Duplicate_release_is_rejected_without_losing_the_current_one()
    {
        var duplicate = await _server.Admin().PostJsonAsync("/api/admin/releases/restic", new CreateReleaseRequest(
            "0.19.1", "linux_amd64", "https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_linux_amd64.bz2",
            "f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _server.CreateClient().GetAsync("/api/tools/restic/manifest?platform=linux_amd64")).StatusCode);
    }

    [Fact]
    public async Task Admin_api_requires_the_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _server.CreateClient().GetAsync("/api/admin/agents")).StatusCode);

        var wrong = _server.CreateClient();
        wrong.DefaultRequestHeaders.Add(Auth.AuthConstants.AdminKeyHeader, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/admin/agents")).StatusCode);
    }

    [Fact]
    public async Task Manifest_endpoint_is_anonymous()
    {
        var manifest = await (await _server.CreateClient().GetAsync("/api/tools/restic/manifest")).ReadAsync<ToolManifestDto>();
        Assert.Equal("da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d", manifest.Sha256);
    }
}
