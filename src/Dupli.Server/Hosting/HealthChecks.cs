using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dupli.Server.Hosting;

/// <summary>
/// <c>/health</c> (liveness, mapped separately in <c>Program.cs</c>) never touches the database: a slow or
/// unreachable PostgreSQL must not make the container restart loop. <c>/health/ready</c> does, so the
/// orchestrator stops routing traffic to a replica that cannot serve requests.
/// </summary>
public static class HealthChecks
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddDupliHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag]).Services;
}

/// <summary><c>SELECT 1</c> through EF Core, cut short so an unreachable database fails the check quickly.</summary>
public sealed class DatabaseHealthCheck(DupliDbContext db) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable", ex);
        }
    }
}
