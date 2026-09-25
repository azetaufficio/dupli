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
            // The password comes from a per-job JobCredentials, released when the job ends: a pooled connection
            // would keep it reachable through Npgsql's internal state well past that.
            Pooling = false,
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
            throw Map(source, ex, password);
        }
    }

    /// <summary>Maps an Npgsql failure; anything else is returned unchanged. <paramref name="password"/>, if given,
    /// is redacted from the message: defensive, Npgsql does not normally echo it back.</summary>
    public static Exception Map(PostgresSourceDto source, Exception ex, string? password = null) => ex switch
    {
        PostgresException pg when pg.SqlState is "28P01" or "28000" =>
            BackupException.Permanent(Redact($"PostgreSQL authentication failed for {source.Username}: {pg.MessageText}", password), ex),
        NpgsqlException npgsql when npgsql.IsTransient =>
            BackupException.Transient(Redact($"PostgreSQL {source.Host}:{source.Port} unreachable: {ex.Message}", password), ex),
        NpgsqlException => BackupException.Permanent(Redact($"PostgreSQL error: {ex.Message}", password), ex),
        _ => ex,
    };

    private static string Redact(string message, string? password) =>
        SecretRedaction.Redact(message, [password]);
}
