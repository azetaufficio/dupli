using System.Net;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dupli.Server.Tests;

/// <summary>
/// <c>/health</c> is liveness only (never touches the database); <c>/health/ready</c> runs a <c>SELECT 1</c>
/// and must fail when PostgreSQL is not reachable.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class HealthTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    [Fact]
    public async Task Both_endpoints_are_ok_with_the_database_reachable()
    {
        Assert.Equal(HttpStatusCode.OK, (await _server.CreateClient().GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _server.CreateClient().GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Readiness_is_503_with_an_unreachable_database_while_liveness_stays_ok()
    {
        // Migrations at startup use the working connection string (Program.cs reads it directly from
        // configuration); only the DbContext used by requests is rebound to an address nothing listens on.
        await using var broken = new BrokenDatabaseServer(postgres);

        Assert.Equal(HttpStatusCode.OK, (await broken.CreateClient().GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await broken.CreateClient().GetAsync("/health/ready")).StatusCode);
    }

    private sealed class BrokenDatabaseServer(PostgresFixture postgres) : WebApplicationFactory<Program>
    {
        private readonly string _connectionString = postgres.ConnectionString($"dupli_{Guid.NewGuid():N}");
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dupli-tests", "server", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Dupli", _connectionString);
            builder.UseSetting("Dupli:RunBackgroundServices", "false");
            builder.UseSetting("DataProtection:FileSystem:Path", Path.Combine(_dataDir, "keys"));
            builder.UseSetting("Dupli:ToolMirrorPath", Path.Combine(_dataDir, "tools"));
            builder.UseSetting("Dupli:RateLimiting:PermitLimit", "100000");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<DupliDbContext>>();
                services.AddDbContext<DupliDbContext>(o => o
                    .UseNpgsql("Host=127.0.0.1;Port=1;Database=nope;Timeout=1;Command Timeout=1")
                    .UseSnakeCaseNamingConvention());
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(_connectionString));
            try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
        }
    }
}
