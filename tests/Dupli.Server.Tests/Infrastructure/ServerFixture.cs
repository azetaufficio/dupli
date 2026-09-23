using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dupli.Contracts;
using Dupli.Contracts.Agents;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Monitoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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

    private readonly string _connectionString;
    private readonly string _authMode;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dupli-tests", "server", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public CapturingNotificationChannel Notifications { get; } = new();

    public DupliTestServer(PostgresFixture postgres, string authMode = "None")
    {
        _connectionString = postgres.ConnectionString($"dupli_{Guid.NewGuid():N}");
        _authMode = authMode;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Dupli", _connectionString);
        builder.UseSetting("Dupli:RunBackgroundServices", "false");
        builder.UseSetting("Dupli:DataProtectionKeysPath", Path.Combine(_dataDir, "keys"));
        builder.UseSetting("Dupli:ToolMirrorPath", Path.Combine(_dataDir, "tools"));
        builder.UseSetting("Dupli:Admin:ApiKey", AdminKey);
        builder.UseSetting("Dupli:PublicUrl", "https://dupli.test");
        builder.UseSetting("Dupli:Auth:Mode", _authMode);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<INotificationChannel>(Notifications);
        });
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
