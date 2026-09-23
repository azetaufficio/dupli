using Dupli.Agent.Core.Errors;
using Dupli.Contracts.Policies;
using Npgsql;

namespace Dupli.Agent.Core.Postgres;

/// <summary>Opens a connection for a policy source, mapping failures to transient/permanent backup errors.</summary>
public static class PostgresConnection
{
    public static async Task<NpgsqlConnection> OpenAsync(
        PostgresSourceDto source, string password, string database, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = password,
            Database = database,
            Timeout = 15,
            ApplicationName = "Dupli",
        };
        var conn = new NpgsqlConnection(cs.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (Exception ex)
        {
            await conn.DisposeAsync();
            throw Map(source, ex);
        }
    }

    /// <summary>Maps an Npgsql failure; anything else is returned unchanged.</summary>
    public static Exception Map(PostgresSourceDto source, Exception ex) => ex switch
    {
        PostgresException pg when pg.SqlState is "28P01" or "28000" =>
            BackupException.Permanent($"PostgreSQL authentication failed for {source.Username}: {pg.MessageText}", ex),
        NpgsqlException npgsql when npgsql.IsTransient =>
            BackupException.Transient($"PostgreSQL {source.Host}:{source.Port} unreachable: {ex.Message}", ex),
        NpgsqlException => BackupException.Permanent($"PostgreSQL error: {ex.Message}", ex),
        _ => ex,
    };
}
