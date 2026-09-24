using System.Text.Json;
using Dupli.Contracts;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Jobs;

/// <summary>Resolves a stored policy into the self-contained spec sent to the agent.</summary>
public static class PolicySpecBuilder
{
    public static PolicySpecDto Build(BackupPolicy policy, IReadOnlyDictionary<Guid, PgConnection> connections) => new()
    {
        PolicyId = policy.Id.ToString(),
        Name = policy.Name,
        Sources = policy.Sources.OrderBy(s => s.SourceKey, StringComparer.Ordinal).Select(s => ToAgentDto(s, connections)).ToList(),
        Retention = Retention(policy),
    };

    public static RetentionDto Retention(BackupPolicy policy) => new()
    {
        KeepDaily = policy.KeepDaily,
        KeepWeekly = policy.KeepWeekly,
        KeepMonthly = policy.KeepMonthly,
    };

    /// <summary>The agent's PostgreSQL connections, keyed by id (one query per resolution).</summary>
    public static async Task<IReadOnlyDictionary<Guid, PgConnection>> LoadConnectionsAsync(DupliDbContext db, Guid agentId, CancellationToken ct) =>
        await db.PgConnections.AsNoTracking().Where(c => c.AgentId == agentId).ToDictionaryAsync(c => c.Id, ct);

    /// <summary>Admin view of a stored source (for <see cref="PolicyDto"/>): no connection resolution.</summary>
    public static PolicySourceDto ToDto(BackupSource source) =>
        (JsonSerializer.Deserialize<PolicySourceDto>(source.Spec, DupliJson.Options)
            ?? throw new InvalidOperationException($"Source {source.Id} has an empty spec"))
        with { SourceId = source.SourceKey };

    public static string Serialize(PolicySourceDto source) =>
        JsonSerializer.Serialize(source, DupliJson.Options);

    public static BackupSourceType TypeOf(PolicySourceDto source) => source switch
    {
        PolicyDirectorySourceDto => BackupSourceType.Directory,
        PolicyPostgresSourceDto => BackupSourceType.PostgreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.GetType().Name, "Unknown source type"),
    };

    /// <summary>Resolves a stored source (and its <see cref="PgConnection"/> for postgres) into the agent contract DTO.</summary>
    private static BackupSourceDto ToAgentDto(BackupSource source, IReadOnlyDictionary<Guid, PgConnection> connections) => ToDto(source) switch
    {
        PolicyDirectorySourceDto dir => new DirectorySourceDto { SourceId = dir.SourceId, Paths = dir.Paths, Excludes = dir.Excludes },
        PolicyPostgresSourceDto pg => ToAgentDto(pg, connections),
        var other => throw new ArgumentOutOfRangeException(nameof(source), other.GetType().Name, "Unknown source type"),
    };

    private static PostgresSourceDto ToAgentDto(PolicyPostgresSourceDto pg, IReadOnlyDictionary<Guid, PgConnection> connections)
    {
        var connection = connections.GetValueOrDefault(pg.ConnectionId)
            ?? throw new InvalidOperationException($"Source {pg.SourceId} references unknown connection {pg.ConnectionId}");
        return new PostgresSourceDto
        {
            SourceId = pg.SourceId,
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            PasswordSecret = connection.PasswordSecret,
            BinDirectory = connection.BinDirectory,
            DatabaseSelection = pg.DatabaseSelection,
            ExcludeDatabases = pg.ExcludeDatabases,
            IncludeDatabases = pg.IncludeDatabases,
            IncludeGlobals = pg.IncludeGlobals,
        };
    }
}
