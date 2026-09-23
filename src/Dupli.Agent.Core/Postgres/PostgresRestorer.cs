using System.Text.RegularExpressions;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Processes;
using Dupli.Contracts.Policies;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dupli.Agent.Core.Postgres;

/// <summary>
/// Loads a custom-format dump into a database created for the purpose. Never touches an existing database:
/// no <c>DROP</c>, no restore over data, globals are never applied.
/// </summary>
public sealed partial class PostgresRestorer(
    IPostgresBinLocator binLocator,
    IProcessRunner runner,
    ILogger<PostgresRestorer> logger)
{
    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>Letters, digits, '_' and '-', not starting with a digit or '-': no quoting surprises, max 63 bytes.</summary>
    public static bool IsValidDatabaseName(string name) => DatabaseNameRegex().IsMatch(name);

    /// <returns>pg_restore warnings (non-zero exit: some objects failed, the database exists and holds the rest).</returns>
    public async Task<IReadOnlyList<string>> RestoreAsync(
        PostgresSourceDto source,
        string password,
        string dumpFile,
        string newDatabase,
        CancellationToken ct)
    {
        if (!IsValidDatabaseName(newDatabase))
            throw BackupException.Permanent($"Invalid database name '{newDatabase}'");
        if (!File.Exists(dumpFile))
            throw BackupException.Permanent($"Restored dump not found at {dumpFile}");

        var dir = source.BinDirectory ?? binLocator.FindBinDirectory(IPostgresBinLocator.Newest);
        var pgRestore = dir is null ? null : Path.Combine(dir, "pg_restore" + ExeSuffix);
        if (pgRestore is null || !File.Exists(pgRestore))
            throw BackupException.Permanent("pg_restore not found; set BinDirectory on the source");

        await CreateDatabaseAsync(source, password, newDatabase, ct);

        var env = new Dictionary<string, string> { ["PGPASSWORD"] = password };
        var result = await runner.RunAsync(new ProcessSpec
        {
            FileName = pgRestore,
            Arguments = ["-h", source.Host, "-p", source.Port.ToString(), "-U", source.Username, "--no-password",
                "--dbname", newDatabase, dumpFile],
            Environment = env,
        }, null, ct);

        if (result.ExitCode == 0)
        {
            logger.LogInformation("Dump {Dump} restored into new database {Database}", dumpFile, newDatabase);
            return [];
        }

        logger.LogWarning("pg_restore into {Database} exited with {Code}", newDatabase, result.ExitCode);
        return [$"pg_restore exited with {result.ExitCode}; some objects may be missing", .. result.StderrTail];
    }

    private static async Task CreateDatabaseAsync(PostgresSourceDto source, string password, string name, CancellationToken ct)
    {
        await using var conn = await PostgresConnection.OpenAsync(source, password, "postgres", ct);
        try
        {
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", conn);
            exists.Parameters.AddWithValue("name", name);
            if (await exists.ExecuteScalarAsync(ct) is not null)
                throw BackupException.Permanent($"Database '{name}' already exists: a restore never writes into an existing database");

            // The name is validated above; identifiers cannot be bound as parameters.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await create.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException ex)
        {
            throw PostgresConnection.Map(source, ex);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,62}$")]
    private static partial Regex DatabaseNameRegex();
}
