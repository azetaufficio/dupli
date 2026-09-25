using System.Net;
using System.Net.Http.Json;
using Dupli.Contracts.Agents;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Tests.Infrastructure;

namespace Dupli.Server.Tests;

/// <summary>
/// Admin-side S3 key rotation: the endpoint (Owner only), the version bump, the secret never appearing in the
/// admin DTO. There is no agent-facing "fetch the storage credentials" endpoint any more: the agent picks up
/// the current key with the next job's just-in-time credentials (see <c>JobCredentialsTests</c>).
/// Verification against the real repository (422 on the wrong key) is covered end to end in
/// <c>Dupli.IntegrationTests</c>, where a real restic binary and S3 (RustFS) are available.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class StorageCredentialsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres, authMode: "EntraId");

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private Task<HttpClient> OwnerAsync() => _server.SignInAsync(DupliTestServer.BootstrapOwnerEmail);

    private static async Task<string> XsrfTokenAsync(HttpClient browser, string url = "/bff/user")
    {
        var response = await browser.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(OperatorAuth.XsrfCookie + "="));
        return Uri.UnescapeDataString(cookie.Split(';')[0][(OperatorAuth.XsrfCookie.Length + 1)..]);
    }

    /// <summary>An unsafe request as Angular sends it: JSON body plus the antiforgery header.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient browser, HttpMethod method, string url, object? body = null)
    {
        using var message = new HttpRequestMessage(method, url);
        if (body is not null)
            message.Content = JsonContent.Create(body, options: Contracts.DupliJson.Options);
        message.Headers.Add(OperatorAuth.XsrfHeader, await XsrfTokenAsync(browser));
        return await browser.SendAsync(message);
    }

    private async Task<HttpClient> InvitedAsync(string email, OperatorRole role)
    {
        await SendAsync(await OwnerAsync(), HttpMethod.Post, "/api/admin/users", new InviteOperatorRequest(email, role));
        return await _server.SignInAsync(email);
    }

    [Fact]
    public async Task Owner_can_update_credentials_with_skip_verification_and_the_heartbeat_reports_the_bumped_version()
    {
        var agent = await _server.EnrollAsync();
        var owner = await OwnerAsync();

        var response = await SendAsync(owner, HttpMethod.Put, $"/api/admin/agents/{agent.AgentId}/storage-credentials",
            new UpdateAgentStorageCredentialsRequest { AccessKeyId = "new-key", SecretAccessKey = "new-secret", SkipVerification = true });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The admin DTO shows the new key id and the pending version, never the secret.
        var details = await (await owner.GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.Equal("new-key", details.S3AccessKeyId);
        Assert.Equal(2, details.S3CredentialsVersion);
        Assert.Equal(1, details.S3CredentialsAppliedVersion); // still the version applied at enrollment
        Assert.NotNull(details.S3CredentialsUpdatedAt);
        var raw = await (await owner.GetAsync($"/api/admin/agents/{agent.AgentId}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("new-secret", raw);

        // Heartbeat still on version 1: the server tells it a newer key exists. The agent itself no longer
        // fetches or applies it out of band any more: the next job's credentials already carry "new-secret"
        // (see JobCredentialsTests), and there is no more agent-facing endpoint to fetch it ahead of a job.
        var heartbeat = await (await agent.Client.PostJsonAsync("/api/agents/heartbeat",
            new HeartbeatRequest { Hostname = "vm-01", Version = "0.1.0", OsVersion = "Windows" }))
            .ReadAsync<HeartbeatResponse>();
        Assert.Equal(2, heartbeat.StorageCredentialsVersion);
    }

    [Fact]
    public async Task There_is_no_agent_facing_endpoint_to_fetch_storage_credentials_ahead_of_a_job()
    {
        var agent = await _server.EnrollAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Client.GetAsync("/api/agents/storage-credentials")).StatusCode);
    }

    [Fact]
    public async Task Missing_fields_are_rejected_and_an_unknown_agent_is_404()
    {
        var agent = await _server.EnrollAsync();
        var owner = await OwnerAsync();

        var empty = await SendAsync(owner, HttpMethod.Put, $"/api/admin/agents/{agent.AgentId}/storage-credentials",
            new UpdateAgentStorageCredentialsRequest { AccessKeyId = "", SecretAccessKey = "s", SkipVerification = true });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var missing = await SendAsync(owner, HttpMethod.Put, $"/api/admin/agents/{Guid.NewGuid()}/storage-credentials",
            new UpdateAgentStorageCredentialsRequest { AccessKeyId = "k", SecretAccessKey = "s", SkipVerification = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Operators_and_viewers_cannot_update_storage_credentials()
    {
        var agent = await _server.EnrollAsync();
        var request = new UpdateAgentStorageCredentialsRequest { AccessKeyId = "k", SecretAccessKey = "s", SkipVerification = true };

        var operatorClient = await InvitedAsync("operator@dupli.test", OperatorRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(operatorClient, HttpMethod.Put, $"/api/admin/agents/{agent.AgentId}/storage-credentials", request)).StatusCode);

        var viewerClient = await InvitedAsync("viewer@dupli.test", OperatorRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(viewerClient, HttpMethod.Put, $"/api/admin/agents/{agent.AgentId}/storage-credentials", request)).StatusCode);
    }

    [Fact]
    public async Task An_agent_older_than_this_feature_never_reports_an_applied_version()
    {
        var agent = await _server.EnrollAsync();
        // No StorageCredentialsVersion in the request at all: an agent built before this feature.
        await agent.Client.PostJsonAsync("/api/agents/heartbeat", new HeartbeatRequest { Hostname = "vm-01", Version = "0.1.0", OsVersion = "Windows" });

        var details = await (await _server.Admin().GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.Equal(1, details.S3CredentialsVersion);
        Assert.Equal(1, details.S3CredentialsAppliedVersion); // set by enrollment, untouched by the heartbeat
    }
}
