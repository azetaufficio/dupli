using System.Net;
using System.Net.Http.Json;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class OperatorUsersTests(PostgresFixture postgres) : IAsyncLifetime
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
    private static async Task<HttpResponseMessage> SendAsync(HttpClient browser, HttpMethod method, string url, object? body = null, string xsrfFrom = "/bff/user")
    {
        using var message = new HttpRequestMessage(method, url);
        if (body is not null)
            message.Content = JsonContent.Create(body, options: Contracts.DupliJson.Options);
        message.Headers.Add(OperatorAuth.XsrfHeader, await XsrfTokenAsync(browser, xsrfFrom));
        return await browser.SendAsync(message);
    }

    private static async Task<OperatorUserDto> InviteAsync(HttpClient owner, string email, OperatorRole role) =>
        await (await SendAsync(owner, HttpMethod.Post, "/api/admin/users", new InviteOperatorRequest(email, role))).ReadAsync<OperatorUserDto>();

    private async Task<HttpClient> InvitedAsync(string email, OperatorRole role)
    {
        await InviteAsync(await OwnerAsync(), email, role);
        return await _server.SignInAsync(email);
    }

    private static async Task<UserInfoDto> UserAsync(HttpClient browser) =>
        await (await browser.GetAsync("/bff/user")).ReadAsync<UserInfoDto>();

    [Fact]
    public async Task Only_the_bootstrap_email_can_sign_in_while_no_user_exists_and_becomes_owner()
    {
        var refused = await DupliTestServer.TrySignInAsync(_server.Browser(), "someone@dupli.test");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal("/access-denied?email=someone%40dupli.test", refused.Headers.Location?.OriginalString);

        // Matched on preferred_username (UPN) too, case-insensitively.
        var owner = await _server.SignInAsync("owner.alias@dupli.test", preferredUsername: "Owner@Dupli.Test");
        var user = await UserAsync(owner);
        Assert.Equal(OperatorRole.Owner, user.Role);

        var list = await (await owner.GetAsync("/api/admin/users")).ReadAsync<List<OperatorUserDto>>();
        var row = Assert.Single(list);
        Assert.True(row.Bound);
        Assert.Equal("bootstrap", row.CreatedBy);

        // Once a user exists, the bootstrap email is just an email: no second owner from it.
        var other = await DupliTestServer.TrySignInAsync(_server.Browser(), DupliTestServer.BootstrapOwnerEmail, oid: "another-oid");
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Fact]
    public async Task Invitation_binds_to_the_first_identity_then_matches_on_oid_only()
    {
        var owner = await OwnerAsync();
        var invited = await InviteAsync(owner, "Mario@Dupli.test", OperatorRole.Operator);
        Assert.False(invited.Bound);
        Assert.Equal(DupliTestServer.BootstrapOwnerEmail, invited.CreatedBy);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(owner, HttpMethod.Post, "/api/admin/users", new InviteOperatorRequest("mario@dupli.test", OperatorRole.Viewer))).StatusCode);

        var mario = await _server.SignInAsync("mario@dupli.test", oid: "oid-mario");
        Assert.Equal(OperatorRole.Operator, (await UserAsync(mario)).Role);

        // Another Entra ID account now claiming the same address does not get in.
        var impostor = await DupliTestServer.TrySignInAsync(_server.Browser(), "mario@dupli.test", oid: "oid-impostor");
        Assert.Equal(HttpStatusCode.Redirect, impostor.StatusCode);

        // The bound account gets in even after its email changed.
        var renamed = await _server.SignInAsync("mario.rossi@dupli.test", oid: "oid-mario");
        Assert.Equal("mario.rossi@dupli.test", (await UserAsync(renamed)).Email);
    }

    [Fact]
    public async Task Other_tenants_and_unknown_people_are_refused()
    {
        var owner = await OwnerAsync();
        await InviteAsync(owner, "guest@dupli.test", OperatorRole.Viewer);

        var otherTenant = await DupliTestServer.TrySignInAsync(_server.Browser(), "guest@dupli.test", tenantId: "22222222-2222-2222-2222-222222222222");
        Assert.Equal(HttpStatusCode.Redirect, otherTenant.StatusCode);

        var browser = _server.Browser();
        var unknown = await DupliTestServer.TrySignInAsync(browser, "nobody@dupli.test");
        Assert.StartsWith("/access-denied", unknown.Headers.Location?.OriginalString);
        Assert.False((await UserAsync(browser)).Authenticated); // no session cookie
    }

    [Fact]
    public async Task Viewer_reads_only_operator_runs_backups_owner_manages_storage_and_users()
    {
        var viewer = await InvitedAsync("viewer@dupli.test", OperatorRole.Viewer);
        var agent = await _server.CreateAgentAsync();
        var storage = new CreateStorageTargetRequest("s3-b", "https://s3.example.com", "bucket-b", null);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/admin/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/admin/agents/{agent.Id}/snapshots/latest/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(viewer, HttpMethod.Post, $"/api/admin/agents/{agent.Id}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/users")).StatusCode);

        var operatorUser = await InvitedAsync("operator@dupli.test", OperatorRole.Operator);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(operatorUser, HttpMethod.Post, $"/api/admin/agents/{agent.Id}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(operatorUser, HttpMethod.Post, "/api/admin/storage-targets", storage)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(operatorUser, HttpMethod.Post, "/api/admin/users", new InviteOperatorRequest("x@dupli.test", OperatorRole.Viewer))).StatusCode);

        var owner = await OwnerAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(owner, HttpMethod.Post, "/api/admin/storage-targets", storage)).StatusCode);
    }

    [Fact]
    public async Task Role_changes_and_disabling_apply_to_open_sessions()
    {
        var owner = await OwnerAsync();
        var invited = await InviteAsync(owner, "anna@dupli.test", OperatorRole.Viewer);
        var anna = await _server.SignInAsync("anna@dupli.test");

        await (await SendAsync(owner, HttpMethod.Put, $"/api/admin/users/{invited.Id}", new UpdateOperatorRequest(OperatorRole.Operator))).ReadAsync<OperatorUserDto>();
        Assert.Equal(OperatorRole.Operator, (await UserAsync(anna)).Role);

        var disabled = await (await SendAsync(owner, HttpMethod.Post, $"/api/admin/users/{invited.Id}/disable")).ReadAsync<OperatorUserDto>();
        Assert.NotNull(disabled.DisabledAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anna.GetAsync("/api/admin/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await DupliTestServer.TrySignInAsync(_server.Browser(), "anna@dupli.test")).StatusCode);

        await (await SendAsync(owner, HttpMethod.Post, $"/api/admin/users/{invited.Id}/enable")).ReadAsync<OperatorUserDto>();
        await _server.SignInAsync("anna@dupli.test");
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_disabled_or_deleted_and_used_accounts_are_only_disabled()
    {
        var owner = await OwnerAsync();
        var self = Assert.Single(await (await owner.GetAsync("/api/admin/users")).ReadAsync<List<OperatorUserDto>>());

        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(owner, HttpMethod.Put, $"/api/admin/users/{self.Id}", new UpdateOperatorRequest(OperatorRole.Operator))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(owner, HttpMethod.Post, $"/api/admin/users/{self.Id}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(owner, HttpMethod.Delete, $"/api/admin/users/{self.Id}")).StatusCode);

        // A pending owner invitation does not count: it may never be used.
        var pending = await InviteAsync(owner, "second@dupli.test", OperatorRole.Owner);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(owner, HttpMethod.Put, $"/api/admin/users/{self.Id}", new UpdateOperatorRequest(OperatorRole.Operator))).StatusCode);

        await _server.SignInAsync("second@dupli.test");
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(owner, HttpMethod.Put, $"/api/admin/users/{self.Id}", new UpdateOperatorRequest(OperatorRole.Operator))).StatusCode);

        // Bound: disable only. Never used: delete.
        var second = await _server.SignInAsync("second@dupli.test");
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(second, HttpMethod.Delete, $"/api/admin/users/{self.Id}")).StatusCode);
        var typo = await InviteAsync(second, "tpyo@dupli.test", OperatorRole.Viewer);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(second, HttpMethod.Delete, $"/api/admin/users/{typo.Id}")).StatusCode);
        // The "second" invitation has been used meanwhile: it is a real user now.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(second, HttpMethod.Delete, $"/api/admin/users/{pending.Id}")).StatusCode);
    }

    [Fact]
    public async Task Break_glass_session_manages_users_only_and_bypasses_the_last_owner_rule()
    {
        var owner = await OwnerAsync();
        var self = Assert.Single(await (await owner.GetAsync("/api/admin/users")).ReadAsync<List<OperatorUserDto>>());

        var browser = _server.Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.GetAsync("/bff/admin-login")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/bff/admin-login", new BreakGlassLoginRequest("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/bff/admin-session")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await browser.PostAsJsonAsync("/bff/admin-login", new BreakGlassLoginRequest(DupliTestServer.AdminKey))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/admin/agents")).StatusCode);

        var invite = new InviteOperatorRequest("rescue@dupli.test", OperatorRole.Owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/admin/users", invite)).StatusCode); // no antiforgery token
        var created = await (await SendAsync(browser, HttpMethod.Post, "/api/admin/users", invite, xsrfFrom: "/bff/admin-session"))
            .ReadAsync<OperatorUserDto>();
        Assert.Equal("break-glass", created.CreatedBy);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(browser, HttpMethod.Delete, $"/api/admin/users/{self.Id}", xsrfFrom: "/bff/admin-session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/admin/agents")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/bff/admin-logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Every_admin_write_endpoint_requires_the_operator_or_owner_role()
    {
        var endpoints = _server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/admin", StringComparison.Ordinal) == true);

        var unguarded = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            if (methods.All(m => m is "GET" or "HEAD"))
                continue;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToHashSet();
            if (!policies.Overlaps([AuthConstants.OperatorPolicy, AuthConstants.OwnerPolicy, AuthConstants.UsersPolicy]))
                unguarded.Add($"{string.Join(",", methods)} {endpoint.RoutePattern.RawText}");
        }

        Assert.Empty(unguarded);
    }

    [Fact]
    public async Task Language_preference_is_set_persisted_and_rejects_unsupported_values()
    {
        var browser = await OwnerAsync();
        Assert.Null((await UserAsync(browser)).Language);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(browser, HttpMethod.Put, "/api/me/language", new SetLanguageRequest("it"))).StatusCode);
        Assert.Equal("it", (await UserAsync(browser)).Language);

        // Persisted: a fresh sign-in (new session, same operator) still sees it.
        var again = await _server.SignInAsync(DupliTestServer.BootstrapOwnerEmail);
        Assert.Equal("it", (await UserAsync(again)).Language);

        var rejected = await SendAsync(browser, HttpMethod.Put, "/api/me/language", new SetLanguageRequest("fr"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("it", (await UserAsync(browser)).Language);
    }
}

[Collection(ServerCollection.Name)]
public sealed class OperatorBootstrapTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Entra_id_server_without_users_or_bootstrap_email_does_not_start()
    {
        await using var server = new DupliTestServer(postgres, authMode: "EntraId", new Dictionary<string, string?>
        {
            ["Dupli:Auth:BootstrapOwnerEmail"] = "",
        });
        var error = Assert.ThrowsAny<Exception>(() => server.CreateClient());
        Assert.Contains("BootstrapOwnerEmail", error.ToString());
    }

    [Fact]
    public async Task Break_glass_page_is_disabled_without_an_admin_key()
    {
        await using var server = new DupliTestServer(postgres, authMode: "EntraId", new Dictionary<string, string?>
        {
            ["Dupli:Admin:ApiKey"] = "",
        });
        var browser = server.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/bff/admin-login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync("/bff/admin-login", new BreakGlassLoginRequest("x"))).StatusCode);
    }
}
