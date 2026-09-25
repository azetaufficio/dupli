using System.Text.Json;
using Dupli.Contracts;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Api;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Jobs;

/// <summary>
/// Builds the just-in-time credentials for one Running job (<c>POST api/agents/jobs/{jobId}/credentials</c>):
/// the repository (from the agent's escrow) plus only the PostgreSQL passwords that job's sources actually
/// reference, scoped by job type. Never logs a value, only the secret names issued.
/// </summary>
public sealed class JobCredentialsService(
    DupliDbContext db,
    SecretProtector protector,
    ILogger<JobCredentialsService> logger)
{
    public async Task<JobCredentialsResponse> GetAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct) ?? throw ApiException.NotFound("Job");
        if (job.AgentId != agentId)
            throw ApiException.Forbidden("Agents can only fetch credentials for their own jobs");
        if (job.State != JobState.Running)
            throw ApiException.Conflict($"Job {jobId} is {job.State}, not Running");

        var payload = JsonSerializer.Deserialize<JobPayloadDto>(job.Payload, DupliJson.Options)
            ?? throw new InvalidOperationException($"Job {job.Id} has an empty payload");
        var secretNames = SecretNamesFor(payload);

        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId, ct);
        var repository = new JobRepositoryCredentialsDto
        {
            Password = protector.Unprotect(agent.RepositoryPasswordProtected),
            AccessKeyId = agent.S3AccessKeyId,
            SecretAccessKey = protector.Unprotect(agent.S3SecretKeyProtected),
        };

        var postgres = new Dictionary<string, string>();
        if (secretNames.Count > 0)
        {
            var rows = await db.AgentSecrets.AsNoTracking()
                .Where(s => s.AgentId == agentId && secretNames.Contains(s.Name))
                .ToListAsync(ct);
            // A secret not set on the server is simply omitted: only that source fails, like today.
            foreach (var row in rows)
                postgres[row.Name] = protector.Unprotect(row.ValueProtected);
        }

        logger.LogInformation("Issued credentials for job {JobId} (agent {AgentId}): repository + postgres secrets [{Names}]",
            jobId, agentId, string.Join(", ", postgres.Keys));

        return new JobCredentialsResponse { Repository = repository, Postgres = postgres };
    }

    /// <summary>Which named PostgreSQL secrets a job type needs. Backup/restore only; retention, repository
    /// check and restore test never touch a live PostgreSQL connection, only the repository.</summary>
    private static IReadOnlyList<string> SecretNamesFor(JobPayloadDto payload) => payload switch
    {
        BackupJobPayload backup => backup.Policy.Sources.OfType<PostgresSourceDto>()
            .Select(s => s.PasswordSecret).Distinct(StringComparer.Ordinal).ToList(),
        RestoreJobPayload { Postgres: { } pg } => [pg.Source.PasswordSecret],
        RestoreJobPayload => [],
        RetentionJobPayload or RepositoryCheckJobPayload or RestoreTestJobPayload => [],
        RestartAgentJobPayload => throw ApiException.BadRequest("RestartAgent jobs need no credentials"),
        _ => [],
    };
}
