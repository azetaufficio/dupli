using System.Text.Json;
using DbUp;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dupli.Server.Tests;

/// <summary>
/// Exercises 0005_pg_connections.sql in isolation: applies every earlier script, seeds legacy-format
/// PostgreSql backup_source rows by hand (as if written before connections existed), then runs the
/// migration under test and checks the connections created and the specs rewritten.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class PgConnectionMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Legacy_postgres_sources_become_connections_and_specs_are_rewritten()
    {
        var connectionString = postgres.ConnectionString($"dupli_migrate_{Guid.NewGuid():N}");
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        // Apply every script except 0005: the one under test runs against data seeded in the old shape.
        var baseline = DeployChanges.To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly,
                name => name.EndsWith(".sql", StringComparison.Ordinal) && !name.Contains("0005_pg_connections", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();
        var baselineResult = baseline.PerformUpgrade();
        Assert.True(baselineResult.Successful, baselineResult.Error?.ToString());

        var storageTargetId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var sourceMainId = Guid.NewGuid();
        var sourceOtherSecretId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO storage_target (id, name, endpoint, bucket, created_at)
                VALUES (@storageTargetId, 'rustfs', 'http://localhost:9000', 'backups', now());

                INSERT INTO agent (id, name, status, storage_target_id, storage_prefix, s3_access_key_id, s3_secret_key_protected, repository_password_protected, created_at)
                VALUES (@agentId, 'vm-legacy', 'Active', @storageTargetId, 'agents/vm-legacy', 'ak', 'sk-protected', 'pw-protected', now());

                INSERT INTO backup_policy (id, agent_id, name, cron, time_zone, enabled, keep_daily, keep_weekly, keep_monthly, created_at, updated_at)
                VALUES (@policyId, @agentId, 'nightly', '0 2 * * *', 'Europe/Rome', true, 7, 4, 12, now(), now());

                -- Same host/port/username/passwordSecret/binDirectory as the second source below except for the secret.
                INSERT INTO backup_source (id, policy_id, source_key, type, spec)
                VALUES (@sourceMainId, @policyId, 'pg1', 'PostgreSql',
                    '{"type":"postgres","sourceId":"pg1","host":"localhost","port":5432,"username":"postgres","passwordSecret":"pg-main","databaseSelection":"AllExcept","excludeDatabases":[],"includeDatabases":[],"includeGlobals":true,"binDirectory":null}'::jsonb);

                -- Same username@host:port, different secret: a distinct connection, name disambiguated with "-2".
                INSERT INTO backup_source (id, policy_id, source_key, type, spec)
                VALUES (@sourceOtherSecretId, @policyId, 'pg2', 'PostgreSql',
                    '{"type":"postgres","sourceId":"pg2","host":"localhost","port":5432,"username":"postgres","passwordSecret":"pg-other","databaseSelection":"AllExcept","excludeDatabases":[],"includeDatabases":[],"includeGlobals":true,"binDirectory":null}'::jsonb);

                -- Old spec missing host/port entirely: falls back to the DTO defaults localhost/5432.
                INSERT INTO backup_source (id, policy_id, source_key, type, spec)
                VALUES (gen_random_uuid(), @policyId, 'pg3', 'PostgreSql',
                    '{"type":"postgres","sourceId":"pg3","username":"postgres","passwordSecret":"pg-main","databaseSelection":"AllExcept","excludeDatabases":[],"includeDatabases":[],"includeGlobals":true}'::jsonb);
                """;
            cmd.Parameters.AddWithValue("storageTargetId", storageTargetId);
            cmd.Parameters.AddWithValue("agentId", agentId);
            cmd.Parameters.AddWithValue("policyId", policyId);
            cmd.Parameters.AddWithValue("sourceMainId", sourceMainId);
            cmd.Parameters.AddWithValue("sourceOtherSecretId", sourceOtherSecretId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Applies whatever is not yet in the DbUp journal: 0005 only.
        DatabaseMigrator.Migrate(connectionString, NullLogger.Instance);

        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync();

        List<(string Name, string Secret)> connections = [];
        await using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT name, password_secret FROM pg_connection WHERE agent_id = @agentId ORDER BY name";
            cmd.Parameters.AddWithValue("agentId", agentId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                connections.Add((reader.GetString(0), reader.GetString(1)));
        }

        // pg1 and pg3 share host/port/username/passwordSecret/binDirectory (pg3's missing host/port default to
        // the same values): one connection. pg2 differs only by secret: a second, disambiguated connection.
        Assert.Equal(2, connections.Count);
        Assert.Equal(("postgres@localhost:5432", "pg-main"), connections[0]);
        Assert.Equal(("postgres@localhost:5432-2", "pg-other"), connections[1]);

        async Task<string> SpecOf(Guid sourceId)
        {
            await using var cmd = verify.CreateCommand();
            cmd.CommandText = "SELECT spec::text FROM backup_source WHERE id = @id";
            cmd.Parameters.AddWithValue("id", sourceId);
            return (string)(await cmd.ExecuteScalarAsync())!;
        }

        var specMain = await SpecOf(sourceMainId);
        Assert.DoesNotContain("\"host\"", specMain);
        Assert.DoesNotContain("\"port\"", specMain);
        Assert.DoesNotContain("\"username\"", specMain);
        Assert.DoesNotContain("\"passwordSecret\"", specMain);
        Assert.DoesNotContain("\"binDirectory\"", specMain);
        Assert.Contains("\"connectionId\"", specMain);

        var specOther = await SpecOf(sourceOtherSecretId);
        Assert.Contains("\"connectionId\"", specOther);

        static Guid ConnectionIdOf(string spec) => JsonDocument.Parse(spec).RootElement.GetProperty("connectionId").GetGuid();
        Assert.NotEqual(ConnectionIdOf(specMain), ConnectionIdOf(specOther)); // pg-main vs. pg-other: different connections
    }
}
