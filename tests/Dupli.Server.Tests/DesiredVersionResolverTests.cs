using System.Net;
using Dupli.Server.Api;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Tests.Infrastructure;
using Dupli.Server.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

/// <summary>Exercises <see cref="DesiredVersionResolver"/> directly against the database (pin, channel fallback, platform).</summary>
[Collection(ServerCollection.Name)]
public sealed class DesiredVersionResolverTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private async Task RegisterAgentReleaseAsync(string version, string platform, string channel, bool makeCurrent = true) =>
        Assert.Equal(HttpStatusCode.Created, (await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest(version, platform, channel, $"https://example.com/dupli-agent_{version}_{platform}", Sha, makeCurrent))).StatusCode);

    private async Task RegisterResticReleaseAsync(string version, string platform, bool makeCurrent = true) =>
        Assert.Equal(HttpStatusCode.Created, (await _server.Admin().PostJsonAsync("/api/admin/releases/restic",
            new CreateReleaseRequest(version, platform, $"https://example.com/restic_{version}_{platform}.zip", Sha, makeCurrent))).StatusCode);

    private async Task<Agent> LoadAgentAsync(Guid id)
    {
        await using var scope = _server.Services.CreateAsyncScope();
        var db = _server.Scoped<DupliDbContext>(scope);
        return await db.Agents.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<(string? Agent, string? Restic)> ResolveAsync(Guid agentId)
    {
        var agent = await LoadAgentAsync(agentId);
        await using var scope = _server.Services.CreateAsyncScope();
        var resolver = _server.Scoped<DesiredVersionResolver>(scope);
        var (agentRelease, resticRelease) = await resolver.ResolveAsync(agent, CancellationToken.None);
        return (agentRelease?.Version, resticRelease?.Version);
    }

    [Fact]
    public async Task No_agent_release_registered_resolves_to_null()
    {
        var agent = await _server.CreateAgentAsync();
        var (desiredAgent, _) = await ResolveAsync(agent.Id);
        Assert.Null(desiredAgent);
    }

    [Fact]
    public async Task Channel_falls_back_dev_to_beta_to_stable()
    {
        var agent = await _server.CreateAgentAsync();
        await RegisterAgentReleaseAsync("1.0.0", "windows_amd64", "stable");
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin().PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("dev", null, null))).StatusCode);

        // dev has no release of its own: falls back to stable.
        Assert.Equal(("1.0.0", "0.19.1"), await ResolveAsync(agent.Id));

        await RegisterAgentReleaseAsync("1.1.0-beta.1", "windows_amd64", "beta");
        Assert.Equal(("1.1.0-beta.1", "0.19.1"), await ResolveAsync(agent.Id));

        await RegisterAgentReleaseAsync("1.2.0-dev.1", "windows_amd64", "dev");
        Assert.Equal(("1.2.0-dev.1", "0.19.1"), await ResolveAsync(agent.Id));
    }

    [Fact]
    public async Task Beta_channel_does_not_fall_back_to_dev()
    {
        var agent = await _server.CreateAgentAsync();
        await RegisterAgentReleaseAsync("1.2.0-dev.1", "windows_amd64", "dev");
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin().PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("beta", null, null))).StatusCode);

        var (desiredAgent, _) = await ResolveAsync(agent.Id);
        Assert.Null(desiredAgent);
    }

    [Fact]
    public async Task Pin_wins_over_the_channel_and_can_reach_any_channel()
    {
        var agent = await _server.CreateAgentAsync();
        await RegisterAgentReleaseAsync("1.0.0", "windows_amd64", "stable");
        await RegisterAgentReleaseAsync("2.0.0-beta.1", "windows_amd64", "beta");
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin().PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("stable", "2.0.0-beta.1", null))).StatusCode);

        // Downgrade also allowed: pinning to a version older than current stays honored.
        var (desiredAgent, _) = await ResolveAsync(agent.Id);
        Assert.Equal("2.0.0-beta.1", desiredAgent);
    }

    [Fact]
    public async Task Platform_isolates_candidates()
    {
        var agent = await _server.CreateAgentAsync(); // default platform windows_amd64
        await RegisterAgentReleaseAsync("9.0.0", "linux_amd64", "stable");

        var (desiredAgent, _) = await ResolveAsync(agent.Id);
        Assert.Null(desiredAgent);
    }

    [Fact]
    public async Task Resolver_falls_back_to_current_when_the_pinned_version_no_longer_exists()
    {
        // update-settings validates the pin against known releases (covered in AdminApiTests); this exercises the
        // resolver in isolation for a pin that became stale (releases are otherwise never deleted).
        var agent = await _server.CreateAgentAsync();
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = _server.Scoped<DupliDbContext>(scope);
            var tracked = await db.Agents.SingleAsync(a => a.Id == agent.Id);
            tracked.PinnedResticVersion = "9.9.9-does-not-exist";
            await db.SaveChangesAsync();
        }

        var (_, desiredRestic) = await ResolveAsync(agent.Id);
        Assert.Equal("0.19.1", desiredRestic);
    }

    [Fact]
    public async Task Restic_pin_wins_when_it_exists()
    {
        var agent = await _server.CreateAgentAsync();
        await RegisterResticReleaseAsync("0.18.0", "windows_amd64", makeCurrent: false);
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin().PutJsonAsync($"/api/admin/agents/{agent.Id}/update-settings",
            new UpdateAgentSettingsRequest("stable", null, "0.18.0"))).StatusCode);

        var (_, desiredRestic) = await ResolveAsync(agent.Id);
        Assert.Equal("0.18.0", desiredRestic);
    }
}
