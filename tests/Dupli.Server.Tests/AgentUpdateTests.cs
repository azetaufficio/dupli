using System.Net;
using System.Security.Cryptography;
using Dupli.Contracts.Agents;
using Dupli.Server.Api;
using Dupli.Server.Background;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Tests.Infrastructure;

namespace Dupli.Server.Tests;

/// <summary>Heartbeat persistence of the M4 update fields, desired manifests, admin settings, the agent mirror
/// with insecure (dev-only) sources, and the AgentUpdateFailed alert.</summary>
[Collection(ServerCollection.Name)]
public sealed class AgentUpdateTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private async Task RegisterAgentReleaseAsync(string version, string platform, string channel, bool makeCurrent = true) =>
        Assert.Equal(HttpStatusCode.Created, (await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest(version, platform, channel, $"https://example.com/dupli-agent_{version}_{platform}", Sha, makeCurrent))).StatusCode);

    [Fact]
    public async Task Heartbeat_persists_update_fields_and_returns_desired_manifests()
    {
        var agent = await _server.EnrollAsync(); // windows_amd64, stable, no pins
        await RegisterAgentReleaseAsync("1.5.0", "windows_amd64", "stable");

        var heartbeat = new HeartbeatRequest
        {
            Hostname = "vm-01", Version = "1.4.0", ResticVersion = "0.19.1", OsVersion = "Windows Server 2025",
            Platform = "windows_amd64",
            LauncherManaged = true,
            LastUpdate = new UpdateStatusDto { Version = "1.5.0", Outcome = UpdateOutcome.Succeeded, At = DateTimeOffset.UtcNow },
            ResticUpdateError = "s3 unreachable",
        };
        var response = await (await agent.Client.PostJsonAsync("/api/agents/heartbeat", heartbeat)).ReadAsync<HeartbeatResponse>();

        Assert.NotNull(response.DesiredAgent);
        Assert.Equal("1.5.0", response.DesiredAgent!.Version);
        Assert.Equal("https://dupli.test/api/tools/agent/1.5.0/windows_amd64", response.DesiredAgent.DownloadUrl);
        Assert.NotNull(response.DesiredRestic);
        Assert.Equal("0.19.1", response.DesiredRestic!.Version);

        var details = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.True(details.LauncherManaged);
        Assert.Equal("1.5.0", details.LastUpdateVersion);
        Assert.Equal(nameof(UpdateOutcome.Succeeded), details.LastUpdateOutcome);
        Assert.Equal("s3 unreachable", details.ResticUpdateError);
        Assert.Equal("1.5.0", details.DesiredAgentVersion);
        Assert.Equal("0.19.1", details.DesiredResticVersion);
        Assert.Equal("windows_amd64", details.Platform);
        Assert.Equal("stable", details.Channel);
    }

    [Fact]
    public async Task Heartbeat_updates_the_platform_when_the_agent_reports_one()
    {
        var agent = await _server.EnrollAsync();
        var heartbeat = new HeartbeatRequest { Hostname = "vm-01", Version = "1.0.0", OsVersion = "Linux", Platform = "linux_amd64" };

        await agent.Client.PostJsonAsync("/api/agents/heartbeat", heartbeat);

        var details = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.Equal("linux_amd64", details.Platform);
    }

    [Fact]
    public async Task Update_settings_validates_channel_and_pins()
    {
        var agent = await _server.CreateAgentAsync();
        var admin = _server.Admin();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("nightly", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("stable", "9.9.9-unknown", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("stable", null, "9.9.9-unknown"))).StatusCode);

        await RegisterAgentReleaseAsync("3.0.0", "windows_amd64", "beta", makeCurrent: false);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("beta", "3.0.0", "0.19.1"))).StatusCode);

        var details = await (await admin.GetAsync($"/api/admin/agents/{agent.Id}")).ReadAsync<AgentDto>();
        Assert.Equal("beta", details.Channel);
        Assert.Equal("3.0.0", details.PinnedAgentVersion);
        Assert.Equal("0.19.1", details.PinnedResticVersion);
    }

    [Fact]
    public async Task Agent_mirror_downloads_from_a_file_source_only_when_insecure_sources_are_allowed()
    {
        var content = "fake dupli-agent build"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var sourceDir = Path.Combine(Path.GetTempPath(), "dupli-tests", "agent-mirror", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "dupli-agent_9.9.9_linux_amd64");
        await File.WriteAllBytesAsync(sourceFile, content);
        var fileUrl = new Uri(sourceFile).AbsoluteUri;
        try
        {
            // Rejected by the default (secure-only) server.
            Assert.Equal(HttpStatusCode.BadRequest, (await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
                new CreateAgentReleaseRequest("9.9.9", "linux_amd64", "stable", fileUrl, sha, MakeCurrent: false))).StatusCode);

            using var insecure = _server.WithWebHostBuilder(b => b.UseSetting("Dupli:Releases:AllowInsecureSources", "true"));
            var admin = insecure.CreateClient();
            admin.DefaultRequestHeaders.Add(Auth.AuthConstants.AdminKeyHeader, DupliTestServer.AdminKey);

            Assert.Equal(HttpStatusCode.Created, (await admin.PostJsonAsync("/api/admin/releases/agent",
                new CreateAgentReleaseRequest("9.9.9", "linux_amd64", "stable", fileUrl, sha, MakeCurrent: false))).StatusCode);

            var download = await insecure.CreateClient().GetAsync("/api/tools/agent/9.9.9/linux_amd64");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rollback_to_the_desired_version_opens_an_alert_that_clears_on_recovery()
    {
        var agent = await _server.EnrollAsync();
        await RegisterAgentReleaseAsync("2.0.0", "windows_amd64", "stable");

        // The Launcher tried 2.0.0, it crashed, and it is back on the previous (running) version 1.0.0.
        var failedHeartbeat = new HeartbeatRequest
        {
            Hostname = "vm-01", Version = "1.0.0", OsVersion = "Windows", LauncherManaged = true,
            LastUpdate = new UpdateStatusDto { Version = "2.0.0", Outcome = UpdateOutcome.RolledBack, Error = "crash loop", At = _server.Time.GetUtcNow() },
        };
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", failedHeartbeat);
        await _server.TickAsync<AlertEvaluator>();

        var alerts = await (await _server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        var alert = Assert.Single(alerts, a => a.Kind == AlertKind.AgentUpdateFailed);
        Assert.Contains("2.0.0", alert.Message);
        Assert.Contains("crash loop", alert.Message);

        // Now the agent catches up to the desired version: the alert must clear.
        var recoveredHeartbeat = failedHeartbeat with { Version = "2.0.0" };
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", recoveredHeartbeat);
        await _server.TickAsync<AlertEvaluator>();

        var openAlerts = await (await _server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        Assert.DoesNotContain(openAlerts, a => a.Kind == AlertKind.AgentUpdateFailed);
    }

    [Fact]
    public async Task Rollback_alert_clears_when_the_desired_version_changes_instead_of_recovering()
    {
        var agent = await _server.EnrollAsync();
        await RegisterAgentReleaseAsync("2.0.0", "windows_amd64", "stable");

        var failedHeartbeat = new HeartbeatRequest
        {
            Hostname = "vm-01", Version = "1.0.0", OsVersion = "Windows", LauncherManaged = true,
            LastUpdate = new UpdateStatusDto { Version = "2.0.0", Outcome = UpdateOutcome.RolledBack, Error = "boom", At = _server.Time.GetUtcNow() },
        };
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", failedHeartbeat);
        await _server.TickAsync<AlertEvaluator>();
        Assert.Contains(
            await (await _server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>(),
            a => a.Kind == AlertKind.AgentUpdateFailed);

        // Operator publishes a fixed release: the desired version is no longer the one that failed.
        await RegisterAgentReleaseAsync("2.0.1", "windows_amd64", "stable");
        await _server.TickAsync<AlertEvaluator>();

        var openAlerts = await (await _server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        Assert.DoesNotContain(openAlerts, a => a.Kind == AlertKind.AgentUpdateFailed);
    }

    [Fact]
    public async Task Agent_stuck_off_the_desired_version_opens_AgentOutdated_after_the_grace_period()
    {
        await using var server = new DupliTestServer(postgres, settings: new Dictionary<string, string?>
        {
            ["Dupli:Alerts:AgentOutdatedAfter"] = "00:00:01",
        });
        var agent = await server.EnrollAsync(); // registers on 0.1.0, windows_amd64, stable
        Assert.Equal(HttpStatusCode.Created, (await server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("2.0.0", "windows_amd64", "stable", "https://example.com/dupli-agent_2.0.0_windows_amd64", Sha))).StatusCode);

        var heartbeat = new HeartbeatRequest { Hostname = "vm-01", Version = "0.1.0", OsVersion = "Windows" };
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", heartbeat);
        await server.TickAsync<AlertEvaluator>();

        // Just went out of date: still inside the grace period.
        var tooSoon = await (await server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        Assert.DoesNotContain(tooSoon, a => a.Kind == AlertKind.AgentOutdated);

        server.Time.Advance(TimeSpan.FromSeconds(2));
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", heartbeat);
        await server.TickAsync<AlertEvaluator>();

        var alerts = await (await server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        var alert = Assert.Single(alerts, a => a.Kind == AlertKind.AgentOutdated);
        Assert.Contains("2.0.0", alert.Message);

        // Catches up to the desired version: the alert clears.
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", heartbeat with { Version = "2.0.0" });
        await server.TickAsync<AlertEvaluator>();

        var openAlerts = await (await server.Admin().GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        Assert.DoesNotContain(openAlerts, a => a.Kind == AlertKind.AgentOutdated);
    }
}
