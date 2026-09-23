using System.Text.RegularExpressions;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Processes;
using Dupli.Contracts.Policies;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dupli.Agent.Core.Postgres;

/// <summary>One stream to back up: a database dump or the cluster globals.</summary>
public sealed record DatabaseDump(string Database, StdinCommandInput Input);

public interface IDatabaseBackupProvider
{
    /// <summary>
    /// Resolves the dumps to run for a source: globals (optional) plus one custom-format dump per database.
    /// Validates connectivity and pg_dump/server version compatibility.
    /// </summary>
    Task<IReadOnlyList<DatabaseDump>> PlanDumpsAsync(
        PostgresSourceDto source,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>Finds the PostgreSQL bin directory (pg_dump, pg_dumpall, pg_restore).</summary>
public interface IPostgresBinLocator
{
    string? FindBinDirectory(int serverMajorVersion);
}

public sealed partial class PostgresDumpProvider(
    IPostgresBinLocator binLocator,
    IProcessRunner runner,
    ILogger<PostgresDumpProvider> logger) : IDatabaseBackupProvider
{
    public const string GlobalsName = "_globals";
    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";

    public async Task<IReadOnlyList<DatabaseDump>> PlanDumpsAsync(
        PostgresSourceDto source,
        string password,
        CancellationToken cancellationToken)
    {
        var (serverMajor, databases) = await DiscoverAsync(source, password, cancellationToken);

        var binDir = source.BinDirectory ?? binLocator.FindBinDirectory(serverMajor)
            ?? throw BackupException.Permanent($"pg_dump not found for PostgreSQL {serverMajor}; set BinDirectory");
        var pgDump = Path.Combine(binDir, "pg_dump" + ExeSuffix);
        var pgDumpAll = Path.Combine(binDir, "pg_dumpall" + ExeSuffix);
        if (!File.Exists(pgDump))
            throw BackupException.Permanent($"pg_dump not found at {pgDump}");

        var dumpMajor = await GetToolMajorVersionAsync(pgDump, cancellationToken);
        if (dumpMajor < serverMajor)
            throw BackupException.Permanent(
                $"pg_dump {dumpMajor} is older than server {serverMajor}; use a pg_dump >= server version");

        var env = new Dictionary<string, string> { ["PGPASSWORD"] = password };
        var connArgs = new[] { "-h", source.Host, "-p", source.Port.ToString(), "-U", source.Username, "--no-password" };

        var dumps = new List<DatabaseDump>();
        if (source.IncludeGlobals)
            dumps.Add(new DatabaseDump(GlobalsName,
                new StdinCommandInput("globals.sql", pgDumpAll, [.. connArgs, "--globals-only"], env)));

        foreach (var db in databases)
            dumps.Add(new DatabaseDump(db,
                new StdinCommandInput($"{db}.dump", pgDump, [.. connArgs, "--format=custom", "--dbname", db], env)));

        logger.LogInformation("PostgreSQL {Major} on {Host}:{Port}: {Count} databases to dump",
            serverMajor, source.Host, source.Port, databases.Count);
        return dumps;
    }

    private static async Task<(int ServerMajor, IReadOnlyList<string> Databases)> DiscoverAsync(
        PostgresSourceDto source, string password, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = password,
            Database = "postgres",
            Timeout = 15,
            ApplicationName = "Dupli",
        };
        try
        {
            await using var conn = new NpgsqlConnection(cs.ConnectionString);
            await conn.OpenAsync(ct);

            await using var versionCmd = new NpgsqlCommand("SHOW server_version_num", conn);
            var versionNum = int.Parse((string)(await versionCmd.ExecuteScalarAsync(ct))!);

            await using var dbCmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn AND datname <> 'postgres' ORDER BY datname",
                conn);
            var dbs = new List<string>();
            await using (var reader = await dbCmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    dbs.Add(reader.GetString(0));

            var excluded = new HashSet<string>(source.ExcludeDatabases, StringComparer.Ordinal);
            return (versionNum / 10000, dbs.Where(d => !excluded.Contains(d)).ToList());
        }
        catch (PostgresException ex) when (ex.SqlState is "28P01" or "28000")
        {
            throw BackupException.Permanent($"PostgreSQL authentication failed for {source.Username}: {ex.MessageText}", ex);
        }
        catch (NpgsqlException ex) when (ex.IsTransient)
        {
            throw BackupException.Transient($"PostgreSQL {source.Host}:{source.Port} unreachable: {ex.Message}", ex);
        }
        catch (NpgsqlException ex)
        {
            throw BackupException.Permanent($"PostgreSQL discovery failed: {ex.Message}", ex);
        }
    }

    private async Task<int> GetToolMajorVersionAsync(string exe, CancellationToken ct)
    {
        var output = new List<string>();
        var result = await runner.RunAsync(new ProcessSpec { FileName = exe, Arguments = ["--version"] }, output.Add, ct);
        var match = result.ExitCode == 0 ? VersionRegex().Match(string.Join(' ', output)) : Match.Empty;
        return match.Success
            ? int.Parse(match.Groups[1].Value)
            : throw BackupException.Permanent($"Cannot determine version of {exe}");
    }

    // "pg_dump (PostgreSQL) 16.4" / "pg_dump (PostgreSQL) 17beta1"
    [GeneratedRegex(@"\(PostgreSQL\)\s+(\d+)")]
    private static partial Regex VersionRegex();
}

/// <summary>Uses only explicitly known directories; the Windows agent adds registry detection.</summary>
public sealed class StaticPostgresBinLocator(IEnumerable<string> candidates) : IPostgresBinLocator
{
    public string? FindBinDirectory(int serverMajorVersion) =>
        candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump")));
}
