using System.Net;
using System.Net.Http.Json;
using Dupli.Contracts.Agents;
using Dupli.Server.Tests.Infrastructure;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class RateLimitingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres, "Development", new Dictionary<string, string?>
    {
        ["Dupli:RateLimiting:PermitLimit"] = "2",
        ["Dupli:RateLimiting:Window"] = "00:10:00",
    });

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static readonly AgentTokenRequest BadToken = new() { AgentId = Guid.NewGuid().ToString(), AgentSecret = "nope" };

    private Task<HttpResponseMessage> TokenAsync(string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/agents/token")
        {
            Content = JsonContent.Create(BadToken, options: Contracts.DupliJson.Options),
        };
        request.Headers.Add("X-Forwarded-For", ip);
        return _server.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task Anonymous_endpoint_returns_429_per_client_ip_after_the_limit()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync("203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync("203.0.113.1")).StatusCode);

        var rejected = await TokenAsync("203.0.113.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        // Another client is not affected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync("203.0.113.2")).StatusCode);
    }

    [Fact]
    public async Task Login_is_limited_but_authenticated_endpoints_are_not()
    {
        var client = _server.CreateClient(new() { AllowAutoRedirect = false });
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/bff/login")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/bff/login")).StatusCode);

        var admin = _server.Admin();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/agents")).StatusCode);
    }
}
