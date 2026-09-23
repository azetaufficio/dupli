using System.Net;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

/// <summary>Admin release management: agent releases, make-current, and GitHub import (faked HTTP).</summary>
[Collection(ServerCollection.Name)]
public sealed class ReleasesTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    [Theory]
    [InlineData("1.0.0+build", "windows_amd64", "stable")] // "+" not allowed in the version
    [InlineData("1.0.0", "plan9_mips", "stable")] // unknown platform
    [InlineData("1.0.0", "windows_amd64", "nightly")] // unknown channel
    public async Task Agent_release_creation_validates_its_fields(string version, string platform, string channel)
    {
        var response = await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest(version, platform, channel, "https://example.com/dupli-agent", Sha, MakeCurrent: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Agent_release_creation_rejects_http_sources_unless_allowed()
    {
        var response = await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("1.0.0", "windows_amd64", "stable", "http://example.com/dupli-agent", Sha, MakeCurrent: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_agent_release_is_rejected_and_current_is_unaffected()
    {
        Assert.Equal(HttpStatusCode.Created, (await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("1.0.0", "windows_amd64", "stable", "https://example.com/a", Sha, MakeCurrent: true))).StatusCode);

        var duplicate = await _server.Admin().PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("1.0.0", "windows_amd64", "stable", "https://example.com/b", Sha, MakeCurrent: true));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var list = await (await _server.Admin().GetAsync("/api/admin/releases?product=agent")).ReadAsync<List<ReleaseDto>>();
        var current = Assert.Single(list, r => r.IsCurrent);
        Assert.Equal("1.0.0", current.Version);
        Assert.Equal("https://example.com/a", current.SourceUrl);
    }

    [Fact]
    public async Task Make_current_demotes_the_previous_one_of_the_same_product_platform_and_channel_only()
    {
        var admin = _server.Admin();
        var first = await (await admin.PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("1.0.0", "windows_amd64", "stable", "https://example.com/a", Sha, MakeCurrent: true))).ReadAsync<ReleaseDto>();
        var second = await (await admin.PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("1.1.0", "windows_amd64", "stable", "https://example.com/b", Sha, MakeCurrent: false))).ReadAsync<ReleaseDto>();
        // Different channel: must stay untouched by making "second" current.
        var otherChannel = await (await admin.PostJsonAsync("/api/admin/releases/agent",
            new CreateAgentReleaseRequest("2.0.0-beta.1", "windows_amd64", "beta", "https://example.com/c", Sha, MakeCurrent: true))).ReadAsync<ReleaseDto>();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/releases/{second.Id}/make-current", null)).StatusCode);

        var list = await (await admin.GetAsync("/api/admin/releases?product=agent")).ReadAsync<List<ReleaseDto>>();
        Assert.True(list.Single(r => r.Id == second.Id).IsCurrent);
        Assert.False(list.Single(r => r.Id == first.Id).IsCurrent);
        Assert.True(list.Single(r => r.Id == otherChannel.Id).IsCurrent);
    }

    [Fact]
    public async Task Import_registers_the_platforms_found_on_github_and_skips_missing_ones()
    {
        using var server = FakeGitHubServer(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("dupli-agent_1.2.3_windows_amd64.exe.sha256"))
                return ShaResponse("dupli-agent_1.2.3_windows_amd64.exe");
            if (url.EndsWith("dupli-agent_1.2.3_linux_amd64.sha256"))
                return ShaResponse("dupli-agent_1.2.3_linux_amd64");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var response = await AdminClient(server).PostJsonAsync("/api/admin/releases/agent/import",
            new ImportAgentReleaseRequest("1.2.3", "beta", MakeCurrent: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registered = await response.ReadAsync<List<ReleaseDto>>();

        Assert.Equal(2, registered.Count);
        Assert.Contains(registered, r => r.Platform == "windows_amd64" && r.SourceUrl.EndsWith(".exe") && r.IsCurrent);
        Assert.Contains(registered, r => r.Platform == "linux_amd64" && r.IsCurrent);
        Assert.DoesNotContain(registered, r => r.Platform == "linux_arm64");
        Assert.All(registered, r => Assert.Equal("beta", r.Channel));
    }

    [Fact]
    public async Task Import_fails_when_no_platform_asset_is_found()
    {
        using var server = FakeGitHubServer(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var response = await AdminClient(server).PostJsonAsync("/api/admin/releases/agent/import",
            new ImportAgentReleaseRequest("9.9.9", "stable", MakeCurrent: true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private WebApplicationFactory<Program> FakeGitHubServer(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _server.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(AdminApi.GitHubClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeHandler(respond))));

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthConstants.AdminKeyHeader, DupliTestServer.AdminKey);
        return client;
    }

    private static HttpResponseMessage ShaResponse(string assetName) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{Sha}  {assetName}\n") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
