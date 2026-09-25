using System.Net;
using System.Net.Http.Json;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Background;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Tests.Infrastructure;
using Dupli.Server.Domain.Operators;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Tests;

[Collection(ServerCollection.Name)]
public sealed class WebUiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres, authMode: "EntraId");

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private HttpClient Browser() => _server.Browser();

    private Task<HttpClient> SignInAsync() => _server.SignInAsync(DupliTestServer.BootstrapOwnerEmail);

    /// <summary>What Angular does: read the XSRF-TOKEN cookie issued by /bff/user and echo it in a header.</summary>
    private static async Task<string> XsrfTokenAsync(HttpClient browser)
    {
        var response = await browser.GetAsync("/bff/user");
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(OperatorAuth.XsrfCookie + "="));
        return Uri.UnescapeDataString(cookie.Split(';')[0][(OperatorAuth.XsrfCookie.Length + 1)..]);
    }

    [Fact]
    public async Task Anonymous_api_calls_get_401_not_a_login_redirect()
    {
        var response = await Browser().GetAsync("/api/admin/agents");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var user = await (await Browser().GetAsync("/bff/user")).ReadAsync<UserInfoDto>();
        Assert.False(user.Authenticated);
        Assert.Equal("EntraId", user.Mode);
    }

    [Fact]
    public async Task Session_cookie_grants_access_and_mutations_need_the_antiforgery_token()
    {
        var browser = await SignInAsync();

        var user = await (await browser.GetAsync("/bff/user")).ReadAsync<UserInfoDto>();
        Assert.True(user.Authenticated);
        Assert.Equal(OperatorRole.Owner, user.Role);
        Assert.Equal("owner", user.Name);
        Assert.Equal(DupliTestServer.BootstrapOwnerEmail, user.Email);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/admin/agents")).StatusCode);

        var request = new CreateStorageTargetRequest("s3", "https://s3.example.com", "bucket", null);
        var withoutToken = await browser.PostJsonAsync("/api/admin/storage-targets", request);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);

        var token = await XsrfTokenAsync(browser);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/admin/storage-targets")
        {
            Content = JsonContent.Create(request, options: Contracts.DupliJson.Options),
        };
        message.Headers.Add(OperatorAuth.XsrfHeader, token);
        Assert.Equal(HttpStatusCode.Created, (await browser.SendAsync(message)).StatusCode);
    }

    [Fact]
    public async Task Admin_key_clients_do_not_need_antiforgery()
    {
        var response = await _server.Admin().PostJsonAsync("/api/admin/storage-targets",
            new CreateStorageTargetRequest("s3", "https://s3.example.com", "bucket", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Logout_requires_the_token_and_ends_the_session()
    {
        var browser = await SignInAsync();
        var token = await XsrfTokenAsync(browser);

        var forged = await browser.PostAsync("/bff/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var logout = await browser.PostAsync("/bff/logout",
            new FormUrlEncodedContent([new(OperatorAuth.XsrfFormField, token)]));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/admin/agents")).StatusCode);
    }

    [Fact]
    public async Task Login_challenges_entra_id_and_refuses_open_redirects()
    {
        Assert.Equal("/agents", await ChallengeRedirectUriAsync("/bff/login?returnUrl=/agents"));
        Assert.Equal("/", await ChallengeRedirectUriAsync("/bff/login?returnUrl=//evil.example.com/"));

        var reselect = await Browser().GetAsync("/bff/login?selectAccount=true");
        Assert.Equal("select_account", QueryHelpers.ParseQuery(reselect.Headers.Location!.Query)["prompt"]);
    }

    /// <summary>Where the browser lands after Entra ID: the redirect URI carried in the (protected) OIDC state.</summary>
    private async Task<string?> ChallengeRedirectUriAsync(string url)
    {
        var challenge = await Browser().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith("https://login.test/authorize", challenge.Headers.Location!.AbsoluteUri);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location.Query)["state"];
        var oidc = _server.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OperatorAuth.OidcScheme);
        return oidc.StateDataFormat.Unprotect(state)?.RedirectUri;
    }

    [Fact]
    public async Task Unknown_api_path_is_404_and_security_headers_are_set()
    {
        var response = await _server.Admin().GetAsync("/api/admin/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Dashboard_counts_agents_and_running_backups()
    {
        var online = await _server.EnrollAsync("vm-01", "m-1");
        await _server.CreateAgentAsync("vm-pending");
        var admin = _server.Admin();
        await online.Client.PostJsonAsync("/api/agents/heartbeat", new Contracts.Agents.HeartbeatRequest { Hostname = "h", Version = "v", OsVersion = "o" });

        var policy = await (await admin.PostJsonAsync($"/api/admin/agents/{online.AgentId}/policies", new PolicyRequest
        {
            Name = "docs",
            Cron = "0 2 * * *",
            Sources = [new PolicyDirectorySourceDto { SourceId = "docs", Paths = ["/data"] }],
        })).ReadAsync<PolicyDto>();
        await admin.PostAsync($"/api/admin/policies/{policy.Id}/run", null);
        await online.Client.GetAsync($"/api/agents/{online.AgentId}/jobs");

        var dashboard = await (await admin.GetAsync("/api/admin/dashboard")).ReadAsync<DashboardDto>();
        Assert.Equal(1, dashboard.Counters.Online);
        Assert.Equal(1, dashboard.Counters.Pending);
        Assert.Equal(1, dashboard.Counters.BackupRunning);
        var row = Assert.Single(dashboard.Agents, a => a.Agent.Id == online.AgentId);
        Assert.Equal(nameof(JobType.Backup), row.RunningJob);
    }

    [Fact]
    public async Task Cron_preview_lists_next_occurrences_or_the_error()
    {
        var admin = _server.Admin();
        var ok = await (await admin.GetAsync("/api/admin/cron/preview?cron=0%202%20*%20*%20*&timeZone=Europe/Rome&count=3")).ReadAsync<CronPreviewDto>();
        Assert.True(ok.Valid);
        Assert.Equal(3, ok.Next.Count);

        var bad = await (await admin.GetAsync("/api/admin/cron/preview?cron=nope")).ReadAsync<CronPreviewDto>();
        Assert.False(bad.Valid);
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public async Task Restore_test_carries_the_policies_stores_item_results_and_alerts_on_failure()
    {
        var agent = await _server.EnrollAsync();
        var admin = _server.Admin();
        await admin.PostJsonAsync($"/api/admin/agents/{agent.AgentId}/policies", new PolicyRequest
        {
            Name = "docs",
            Cron = "0 2 * * *",
            Sources = [new PolicyDirectorySourceDto { SourceId = "docs", Paths = ["/data"] }],
        });

        var job = await (await admin.PostJsonAsync($"/api/admin/agents/{agent.AgentId}/jobs", new RunSystemJobRequest(JobType.RestoreTest))).ReadAsync<JobDto>();
        var polled = Assert.Single(await (await agent.Client.GetAsync($"/api/agents/{agent.AgentId}/jobs")).ReadAsync<List<AgentJobDto>>());
        var payload = Assert.IsType<RestoreTestJobPayload>(polled.Payload);
        Assert.Equal("docs", Assert.Single(payload.Policies).Name);

        await agent.Client.PostJsonAsync($"/api/jobs/{job.Id}/failed", new JobResultDto
        {
            Outcome = JobOutcome.Failed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Error = "1 of 2 checks failed",
            Items =
            [
                new JobItemResultDto { SourceId = "docs", Item = "/data/a.txt", Outcome = JobOutcome.Succeeded, SnapshotId = "s1" },
                new JobItemResultDto { SourceId = "docs", Item = "/data/b.txt", Outcome = JobOutcome.Failed, Error = "size mismatch" },
            ],
        });

        var tests = await (await admin.GetAsync($"/api/admin/jobs?agentId={agent.AgentId}&type=RestoreTest")).ReadAsync<PagedDto<JobDto>>();
        var stored = Assert.Single(tests.Items);
        Assert.Equal(JobState.Failed, stored.State);
        Assert.Equal(2, stored.Items.Count);

        await _server.TickAsync<AlertEvaluator>();
        var alerts = await (await admin.GetAsync($"/api/admin/alerts?agentId={agent.AgentId}")).ReadAsync<List<AlertDto>>();
        Assert.Contains(alerts, a => a.Kind == AlertKind.RestoreTestFailed);
    }

    [Fact]
    public async Task Restart_request_expires_quickly_when_the_agent_does_not_poll()
    {
        var agent = await _server.EnrollAsync();
        var admin = _server.Admin();
        var job = await (await admin.PostJsonAsync($"/api/admin/agents/{agent.AgentId}/jobs", new RunSystemJobRequest(JobType.RestartAgent))).ReadAsync<JobDto>();
        Assert.True(job.ExpiresAt - job.ScheduledAt <= TimeSpan.FromMinutes(15));

        _server.Time.Advance(TimeSpan.FromMinutes(16));
        await _server.TickAsync<JobSweeper>();
        Assert.Equal(JobState.Missed, (await (await admin.GetAsync($"/api/admin/jobs/{job.Id}")).ReadAsync<JobDto>()).State);
    }

    [Fact]
    public async Task Disabled_agent_can_be_re_enabled_for_a_new_enrollment()
    {
        var agent = await _server.EnrollAsync();
        var admin = _server.Admin();
        await admin.PostAsync($"/api/admin/agents/{agent.AgentId}/disable", null);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/agents/{agent.AgentId}/enrollment-tokens", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/agents/{agent.AgentId}/enable", null)).StatusCode);
        var reloaded = await (await admin.GetAsync($"/api/admin/agents/{agent.AgentId}")).ReadAsync<AgentDto>();
        Assert.Equal(Domain.Agents.AgentStatus.Pending, reloaded.Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/agents/{agent.AgentId}/enrollment-tokens", null)).StatusCode);
    }
}
