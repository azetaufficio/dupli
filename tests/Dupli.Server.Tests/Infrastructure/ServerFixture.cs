using System.Net.Http.Headers;
using System.Security.Claims;
using System.Net.Http.Json;
using Dupli.Contracts;
using Dupli.Contracts.Agents;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Monitoring;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testcontainers.PostgreSql;

namespace Dupli.Server.Tests.Infrastructure;

/// <summary>One PostgreSQL container per test run; every <see cref="DupliTestServer"/> gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string ConnectionString(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "server";
}

public sealed class CapturingNotificationChannel : INotificationChannel
{
    public List<Notification> Sent { get; } = [];

    public Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        lock (Sent)
            Sent.Add(notification);
        return Task.CompletedTask;
    }
}

/// <summary>In-process server with a fake clock, no background workers, a fresh database and an admin key.</summary>
public sealed class DupliTestServer : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string BootstrapOwnerEmail = "owner@dupli.test";

    private readonly string _connectionString;
    private readonly string _authMode;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dupli-tests", "server", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public CapturingNotificationChannel Notifications { get; } = new();

    public DupliTestServer(PostgresFixture postgres, string authMode = "None", IReadOnlyDictionary<string, string?>? settings = null)
    {
        _connectionString = postgres.ConnectionString($"dupli_{Guid.NewGuid():N}");
        _authMode = authMode;
        _settings = settings ?? new Dictionary<string, string?>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Dupli", _connectionString);
        builder.UseSetting("Dupli:RunBackgroundServices", "false");
        builder.UseSetting("DataProtection:FileSystem:Path", Path.Combine(_dataDir, "keys"));
        builder.UseSetting("Dupli:ToolMirrorPath", Path.Combine(_dataDir, "tools"));
        builder.UseSetting("Dupli:Admin:ApiKey", AdminKey);
        builder.UseSetting("Dupli:PublicUrl", "https://dupli.test");
        builder.UseSetting("Dupli:Auth:Mode", _authMode);
        // Every test client shares one (null) remote IP: keep the anonymous rate limit out of the way.
        builder.UseSetting("Dupli:RateLimiting:PermitLimit", "100000");
        if (_authMode == "EntraId")
        {
            builder.UseSetting("Dupli:Auth:EntraId:TenantId", TenantId);
            builder.UseSetting("Dupli:Auth:EntraId:ClientId", "test-client");
            builder.UseSetting("Dupli:Auth:EntraId:ClientSecret", "test-secret");
            builder.UseSetting("Dupli:Auth:BootstrapOwnerEmail", BootstrapOwnerEmail);
        }
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<INotificationChannel>(Notifications);
            if (_authMode == "EntraId")
            {
                // No metadata download: the challenge only needs the authorization endpoint.
                var configuration = new OpenIdConnectConfiguration
                {
                    AuthorizationEndpoint = "https://login.test/authorize",
                    EndSessionEndpoint = "https://login.test/logout",
                };
                services.PostConfigure<OpenIdConnectOptions>(OperatorAuth.OidcScheme, o =>
                {
                    o.Configuration = configuration;
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.AddSingleton<IStartupFilter, FakeEntraSignIn>();
            }
        });
    }

    /// <summary>Browser-like client: keeps cookies, does not follow redirects.</summary>
    public HttpClient Browser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>
    /// Signs in as Entra ID would (EntraId mode): the claims go through the real <see cref="OperatorDirectory"/>.
    /// Returns the browser, or throws with the access-denied redirect when the sign-in is refused.
    /// </summary>
    public async Task<HttpClient> SignInAsync(string email, string? oid = null, string? preferredUsername = null, string tenantId = TenantId)
    {
        var browser = Browser();
        var response = await TrySignInAsync(browser, email, oid, preferredUsername, tenantId);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            throw new HttpRequestException($"Sign-in refused: {(int)response.StatusCode} {response.Headers.Location}");
        return browser;
    }

    public static Task<HttpResponseMessage> TrySignInAsync(
        HttpClient browser, string? email, string? oid = null, string? preferredUsername = null, string tenantId = TenantId)
    {
        var query = new Dictionary<string, string?>
        {
            ["tid"] = tenantId,
            ["oid"] = oid ?? $"oid-{email}",
            ["email"] = email,
            ["preferred_username"] = preferredUsername,
            ["name"] = email?.Split('@')[0],
        };
        return browser.GetAsync(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(FakeEntraSignIn.Path, query));
    }

    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AuthConstants.AdminKeyHeader, AdminKey);
        return client;
    }

    /// <summary>Runs one tick of a background task, as the hosted worker would.</summary>
    public async Task TickAsync<TTask>() where TTask : Background.IPeriodicTask
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TTask>().RunOnceAsync(CancellationToken.None);
    }

    public T Scoped<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // Npgsql keeps idle pooled connections open past disposal; with one database per test that adds up
        // to the shared container's max_connections as the suite grows. Close this instance's pool right away.
        Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(_connectionString));
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Stands in for the Entra ID redirect round trip: <c>GET /test/entra-sign-in?tid=&amp;oid=&amp;email=...</c> hands the
/// claims to <see cref="OperatorDirectory"/> exactly like the OIDC <c>OnTokenValidated</c> event does.
/// Registered only by the test fixture.
/// </summary>
public sealed class FakeEntraSignIn : IStartupFilter
{
    public const string Path = "/test/entra-sign-in";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Map(Path, branch => branch.Run(async http =>
        {
            var claims = new[] { "tid", "oid", "email", "preferred_username", "name" }
                .Where(type => !string.IsNullOrEmpty(http.Request.Query[type]))
                .Select(type => new Claim(type, http.Request.Query[type].ToString()));
            var identity = OperatorIdentity.FromClaims(new ClaimsPrincipal(new ClaimsIdentity(claims, "fake-entra")));
            var session = identity is null
                ? null
                : await http.RequestServices.GetRequiredService<OperatorDirectory>().SignInAsync(identity, http.RequestAborted);
            if (session is null)
            {
                http.Response.Redirect(OperatorAuth.AccessDeniedUrl(identity?.Shown));
                return;
            }
            await http.SignInAsync(OperatorAuth.CookieScheme, session);
            http.Response.StatusCode = StatusCodes.Status204NoContent;
        }));
        next(app);
    };
}

public sealed record EnrolledAgent(Guid AgentId, RegisterAgentResponse Registration, HttpClient Client);

public static class DupliTestServerExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(DupliJson.Options))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, DupliJson.Options);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, DupliJson.Options);

    public static async Task<AgentDto> CreateAgentAsync(this DupliTestServer server, string name = "vm-01")
    {
        var admin = server.Admin();
        var storages = await (await admin.GetAsync("/api/admin/storage-targets")).ReadAsync<List<StorageTargetDto>>();
        var storage = storages.FirstOrDefault() ?? await (await admin.PostJsonAsync("/api/admin/storage-targets",
            new CreateStorageTargetRequest("rustfs", "http://localhost:9000", "backups", "us-east-1"))).ReadAsync<StorageTargetDto>();

        return await (await admin.PostJsonAsync("/api/admin/agents", new CreateAgentRequest
        {
            Name = name,
            StorageTargetId = storage.Id,
            StoragePrefix = $"agents/{name}",
            S3AccessKeyId = $"AK-{name}",
            S3SecretAccessKey = $"SK-{name}",
            RepositoryPassword = $"repo-password-{name}",
        })).ReadAsync<AgentDto>();
    }

    public static async Task<EnrolledAgent> EnrollAsync(this DupliTestServer server, string name = "vm-01", string machineId = "machine-1")
    {
        var agent = await server.CreateAgentAsync(name);
        var token = await (await server.Admin().PostAsync($"/api/admin/agents/{agent.Id}/enrollment-tokens", null))
            .ReadAsync<EnrollmentTokenDto>();

        var anonymous = server.CreateClient();
        var registration = await (await anonymous.PostJsonAsync("/api/agents/register", new RegisterAgentRequest
        {
            EnrollmentToken = token.Token,
            MachineId = machineId,
            Hostname = $"{name}.local",
            OsVersion = "Windows Server 2022",
            AgentVersion = "0.1.0",
        })).ReadAsync<RegisterAgentResponse>();

        var accessToken = await (await anonymous.PostJsonAsync("/api/agents/token", new AgentTokenRequest
        {
            AgentId = registration.AgentId,
            AgentSecret = registration.AgentSecret,
        })).ReadAsync<AgentTokenResponse>();

        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.AccessToken);
        return new EnrolledAgent(Guid.Parse(registration.AgentId), registration, client);
    }
}
