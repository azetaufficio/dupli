using System.Text.Json;
using Dupli.Contracts;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Microsoft.Data.Sqlite;

namespace Dupli.Agent.Server;

public enum LedgerState
{
    Received,
    Running,

    /// <summary>Result stored locally, not yet acknowledged by the server (outbox).</summary>
    Finished,
    Reported,
}

public sealed record PendingReport(string JobId, JobResultDto Result);

/// <summary>
/// Local SQLite ledger (<c>config\agent.db</c>): idempotency (a job id is executed at most once),
/// outbox for results the server has not acknowledged yet, crash recovery, and the last policy
/// specs received (so a CLI restore can still refuse to overwrite source paths).
/// </summary>
public sealed class JobLedger
{
    private readonly string _connectionString;

    public JobLedger(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS job (
                job_id       TEXT PRIMARY KEY,
                type         TEXT NOT NULL,
                state        TEXT NOT NULL,
                received_at  TEXT NOT NULL,
                started_at   TEXT NULL,
                finished_at  TEXT NULL,
                outcome      TEXT NULL,
                result       TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS policy (
                policy_id    TEXT PRIMARY KEY,
                spec         TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );
            """);
    }

    /// <summary>True when the job has never been started here and may run now.</summary>
    public bool TryBegin(string jobId, JobType type, DateTimeOffset now)
    {
        Execute("INSERT OR IGNORE INTO job (job_id, type, state, received_at) VALUES ($id, $type, $state, $now)",
            ("$id", jobId), ("$type", type.ToString()), ("$state", nameof(LedgerState.Received)), ("$now", now.ToString("O")));
        return Scalar("SELECT state FROM job WHERE job_id = $id", ("$id", jobId)) is nameof(LedgerState.Received);
    }

    public void MarkRunning(string jobId, DateTimeOffset now) =>
        Execute("UPDATE job SET state = $state, started_at = $now WHERE job_id = $id",
            ("$id", jobId), ("$state", nameof(LedgerState.Running)), ("$now", now.ToString("O")));

    public void MarkFinished(string jobId, JobResultDto result) =>
        Execute("UPDATE job SET state = $state, finished_at = $at, outcome = $outcome, result = $result WHERE job_id = $id",
            ("$id", jobId), ("$state", nameof(LedgerState.Finished)), ("$at", result.CompletedAt.ToString("O")),
            ("$outcome", result.Outcome.ToString()), ("$result", JsonSerializer.Serialize(result, DupliJson.Options)));

    public void MarkReported(string jobId) =>
        Execute("UPDATE job SET state = $state WHERE job_id = $id", ("$id", jobId), ("$state", nameof(LedgerState.Reported)));

    public LedgerState? GetState(string jobId) =>
        Scalar("SELECT state FROM job WHERE job_id = $id", ("$id", jobId)) is string s ? Enum.Parse<LedgerState>(s) : null;

    /// <summary>Jobs left Running by a previous process: the agent restarted mid-job.</summary>
    public IReadOnlyList<(string JobId, DateTimeOffset StartedAt)> Interrupted()
    {
        var list = new List<(string, DateTimeOffset)>();
        Query("SELECT job_id, started_at, received_at FROM job WHERE state = $state", r =>
            list.Add((r.GetString(0), DateTimeOffset.Parse(r.IsDBNull(1) ? r.GetString(2) : r.GetString(1)))),
            ("$state", nameof(LedgerState.Running)));
        return list;
    }

    public IReadOnlyList<PendingReport> PendingReports()
    {
        var list = new List<PendingReport>();
        Query("SELECT job_id, result FROM job WHERE state = $state ORDER BY finished_at", r =>
            list.Add(new PendingReport(r.GetString(0), JsonSerializer.Deserialize<JobResultDto>(r.GetString(1), DupliJson.Options)!)),
            ("$state", nameof(LedgerState.Finished)));
        return list;
    }

    public IReadOnlyList<string> RunningJobIds()
    {
        var list = new List<string>();
        Query("SELECT job_id FROM job WHERE state = $state", r => list.Add(r.GetString(0)), ("$state", nameof(LedgerState.Running)));
        return list;
    }

    public DateTimeOffset? LastSuccessfulBackupAt() =>
        Scalar("""
            SELECT MAX(finished_at) FROM job
            WHERE type = 'Backup' AND outcome IN ('Succeeded', 'SucceededWithWarnings')
            """) is string s ? DateTimeOffset.Parse(s) : null;

    public void SavePolicy(PolicySpecDto policy, DateTimeOffset now) =>
        Execute("INSERT INTO policy (policy_id, spec, updated_at) VALUES ($id, $spec, $now) " +
                "ON CONFLICT (policy_id) DO UPDATE SET spec = excluded.spec, updated_at = excluded.updated_at",
            ("$id", policy.PolicyId), ("$spec", JsonSerializer.Serialize(policy, DupliJson.Options)), ("$now", now.ToString("O")));

    public IReadOnlyList<PolicySpecDto> KnownPolicies()
    {
        var list = new List<PolicySpecDto>();
        Query("SELECT spec FROM policy ORDER BY policy_id", r =>
            list.Add(JsonSerializer.Deserialize<PolicySpecDto>(r.GetString(0), DupliJson.Options)!));
        return list;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar() is var v and not DBNull ? v : null;
    }

    private void Query(string sql, Action<SqliteDataReader> row, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            row(reader);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }
}
