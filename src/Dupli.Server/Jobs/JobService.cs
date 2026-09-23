using System.Text.Json;
using Dupli.Contracts;
using Dupli.Contracts.Jobs;
using Dupli.Server.Api;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dupli.Server.Jobs;

/// <summary>Job lifecycle: creation (with coalescing), atomic assignment, lease renewal, results, sweeping.</summary>
public sealed class JobService(
    DupliDbContext db,
    IOptions<DupliServerOptions> options,
    TimeProvider time,
    ILogger<JobService> logger)
{
    private JobOptions Jobs => options.Value.Jobs;

    /// <summary>Creates a Backup job for <paramref name="policy"/>, or returns null when one is already pending.</summary>
    public Task<Job?> CreateBackupJobAsync(BackupPolicy policy, JobTrigger trigger, DateTimeOffset scheduledAt, CancellationToken ct) =>
        CreateAsync(policy.AgentId, policy.Id, JobType.Backup, trigger, scheduledAt,
            new BackupJobPayload { Policy = PolicySpecBuilder.Build(policy) }, ct);

    public async Task<Job?> CreateSystemJobAsync(Guid agentId, JobType type, JobTrigger trigger, DateTimeOffset scheduledAt, CancellationToken ct)
    {
        JobPayloadDto payload = type switch
        {
            JobType.Retention => new RetentionJobPayload
            {
                Policies = (await db.Policies.AsNoTracking().Where(p => p.AgentId == agentId).OrderBy(p => p.Name).ToListAsync(ct))
                    .Select(p => new PolicyRetentionDto { PolicyId = p.Id.ToString(), Retention = PolicySpecBuilder.Retention(p) })
                    .ToList(),
            },
            JobType.RepositoryCheck => new RepositoryCheckJobPayload
            {
                ReadDataSubsetPercent = options.Value.Maintenance.CheckReadDataSubsetPercent,
            },
            _ => throw ApiException.BadRequest($"{type} is not a system job type"),
        };
        return await CreateAsync(agentId, null, type, trigger, scheduledAt, payload, ct);
    }

    private async Task<Job?> CreateAsync(
        Guid agentId, Guid? policyId, JobType type, JobTrigger trigger, DateTimeOffset scheduledAt,
        JobPayloadDto payload, CancellationToken ct)
    {
        var pending = await db.Jobs.AnyAsync(j =>
            j.AgentId == agentId && j.PolicyId == policyId && j.Type == type &&
            (j.State == JobState.Pending || j.State == JobState.Assigned), ct);
        if (pending)
        {
            logger.LogInformation("{Type} job for agent {AgentId} / policy {PolicyId} coalesced into the pending one",
                type, agentId, policyId);
            return null;
        }

        var job = new Job
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            PolicyId = policyId,
            Type = type,
            Trigger = trigger,
            Payload = JsonSerializer.Serialize(payload, DupliJson.Options),
            CreatedAt = time.GetUtcNow(),
            ScheduledAt = scheduledAt,
            ExpiresAt = scheduledAt + Jobs.DefaultExpiry,
        };
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race with a concurrent creator: the partial unique index keeps it to one pending job.
            db.Entry(job).State = EntityState.Detached;
            return null;
        }

        logger.LogInformation("Created {Type} job {JobId} ({Trigger}) for agent {AgentId}", type, job.Id, trigger, agentId);
        return job;
    }

    /// <summary>
    /// Returns the job the agent should work on: an already-assigned one (redelivery after a lost response),
    /// or the oldest due Pending job, assigned atomically. Never more than one active job per agent.
    /// </summary>
    public async Task<IReadOnlyList<AgentJobDto>> AssignAsync(Guid agentId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Serializes concurrent polls of the same agent.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM agent WHERE id = {agentId} FOR UPDATE", ct);

        var active = await db.Jobs
            .Where(j => j.AgentId == agentId && (j.State == JobState.Assigned || j.State == JobState.Running))
            .ToListAsync(ct);
        if (active.Count > 0)
        {
            await tx.CommitAsync(ct);
            return active.Where(j => j.State == JobState.Assigned && !j.CancelRequested).Select(ToAgentDto).ToList();
        }

        var candidates = await db.Jobs
            .FromSqlInterpolated($"""
                SELECT * FROM job
                WHERE agent_id = {agentId} AND state = 'Pending' AND NOT cancel_requested
                  AND scheduled_at <= {now} AND expires_at > {now}
                ORDER BY scheduled_at, created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (candidates is not [var job])
        {
            await tx.CommitAsync(ct);
            return [];
        }

        JobStateMachine.Transition(job, JobState.Assigned, now);
        job.LeaseUntil = now + Jobs.LeaseDuration;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Job {JobId} assigned to agent {AgentId}", job.Id, agentId);
        return [ToAgentDto(job)];
    }

    public async Task<JobControlResponse> StartedAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await GetOwnedAsync(agentId, jobId, ct);
        var now = time.GetUtcNow();
        if (job.State == JobState.Assigned)
            JobStateMachine.Transition(job, JobState.Running, now);
        return await RenewAsync(job, now, ct);
    }

    public async Task<JobControlResponse> ProgressAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await GetOwnedAsync(agentId, jobId, ct);
        return await RenewAsync(job, time.GetUtcNow(), ct);
    }

    private async Task<JobControlResponse> RenewAsync(Job job, DateTimeOffset now, CancellationToken ct)
    {
        if (job.IsTerminal)
        {
            // Timed out or cancelled server-side: tell the agent to stop.
            return new JobControlResponse { LeaseUntil = now, CancelRequested = true };
        }

        job.LeaseUntil = now + Jobs.LeaseDuration;
        await db.SaveChangesAsync(ct);
        return new JobControlResponse { LeaseUntil = job.LeaseUntil.Value, CancelRequested = job.CancelRequested };
    }

    /// <summary>Records the agent's final report. Idempotent: a repeated report of a finished job is ignored.</summary>
    public async Task ReportResultAsync(Guid agentId, Guid jobId, JobResultDto result, CancellationToken ct)
    {
        var job = await GetOwnedAsync(agentId, jobId, ct);
        var now = time.GetUtcNow();

        var target = result.Outcome switch
        {
            JobOutcome.Succeeded or JobOutcome.SucceededWithWarnings => JobState.Succeeded,
            JobOutcome.Cancelled => JobState.Cancelled,
            _ => JobState.Failed,
        };
        var error = result.Outcome == JobOutcome.Interrupted
            ? $"Interrupted: {result.Error ?? "agent restarted while the job was running"}"
            : result.Error;

        var alreadyRecorded = job.Type == JobType.Backup && await db.Runs.AnyAsync(r => r.JobId == jobId, ct);
        if (job.IsTerminal && (alreadyRecorded || job.Type != JobType.Backup))
        {
            logger.LogInformation("Duplicate or late result for job {JobId} ({State}) ignored", jobId, job.State);
            return;
        }

        if (!job.IsTerminal)
            JobStateMachine.Transition(job, target, now, error);
        else
            logger.LogWarning("Late result for job {JobId} already {State}: run recorded, state kept", jobId, job.State);

        if (job.Type == JobType.Backup && job.PolicyId is { } policyId)
            await RecordRunAsync(job, policyId, result, error, ct);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Job {JobId} finished: {Outcome}", jobId, result.Outcome);
    }

    private async Task RecordRunAsync(Job job, Guid policyId, JobResultDto result, string? error, CancellationToken ct)
    {
        db.Runs.Add(new BackupRun
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            PolicyId = policyId,
            AgentId = job.AgentId,
            StartedAt = result.StartedAt.ToUniversalTime(),
            CompletedAt = result.CompletedAt.ToUniversalTime(),
            Status = result.Outcome.ToString(),
            BytesProcessed = result.Items.Sum(i => i.BytesProcessed),
            BytesAdded = result.Items.Sum(i => i.BytesAdded),
            Items = JsonSerializer.Serialize(result.Items, DupliJson.Options),
            ErrorMessage = error ?? FirstItemError(result),
        });

        if (result.Outcome is JobOutcome.Succeeded or JobOutcome.SucceededWithWarnings)
        {
            var agent = await db.Agents.FindAsync([job.AgentId], ct);
            if (agent is not null && (agent.LastBackupAt is null || agent.LastBackupAt < result.CompletedAt))
                agent.LastBackupAt = result.CompletedAt.ToUniversalTime();
        }
    }

    private static string? FirstItemError(JobResultDto result) =>
        result.Items.FirstOrDefault(i => i.Error is not null) is { } item ? $"{item.Item}: {item.Error}" : null;

    /// <summary>Operator cancel: immediate when not started, otherwise flagged for the agent to pick up.</summary>
    public async Task<Job> RequestCancelAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.Jobs.FindAsync([jobId], ct) ?? throw ApiException.NotFound("Job");
        if (job.IsTerminal)
            throw ApiException.Conflict($"Job is already {job.State}");

        var now = time.GetUtcNow();
        job.CancelRequested = true;
        if (job.State is JobState.Pending or JobState.Assigned)
            JobStateMachine.Transition(job, JobState.Cancelled, now, "Cancelled by operator");

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Cancel requested for job {JobId} ({State})", jobId, job.State);
        return job;
    }

    /// <summary>Pending past expiry → Missed; lapsed lease or run too long → TimedOut (or back to Pending if never started).</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var maxStart = now - Jobs.MaxRunDuration;
        var stale = await db.Jobs
            .Where(j => (j.State == JobState.Pending && j.ExpiresAt <= now)
                        || ((j.State == JobState.Assigned || j.State == JobState.Running)
                            && (j.LeaseUntil <= now || j.StartedAt <= maxStart)))
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            switch (job.State)
            {
                case JobState.Pending:
                    JobStateMachine.Transition(job, JobState.Missed, now, "Not picked up before expiry");
                    break;
                case JobState.Assigned when job.ExpiresAt > now:
                    JobStateMachine.Transition(job, JobState.Pending, now);
                    break;
                case JobState.Assigned:
                    JobStateMachine.Transition(job, JobState.Missed, now, "Assigned but never started before expiry");
                    break;
                default:
                    JobStateMachine.Transition(job, JobState.TimedOut, now,
                        job.StartedAt <= maxStart ? "Exceeded maximum run duration" : "Agent stopped renewing the lease");
                    break;
            }
            logger.LogWarning("Job {JobId} swept to {State}", job.Id, job.State);
        }

        if (stale.Count > 0)
            await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private async Task<Job> GetOwnedAsync(Guid agentId, Guid jobId, CancellationToken ct) =>
        await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.AgentId == agentId, ct)
        ?? throw ApiException.NotFound("Job");

    private static AgentJobDto ToAgentDto(Job job) => new()
    {
        JobId = job.Id.ToString(),
        Type = job.Type,
        Payload = JsonSerializer.Deserialize<JobPayloadDto>(job.Payload, DupliJson.Options)
            ?? throw new InvalidOperationException($"Job {job.Id} has an empty payload"),
        ScheduledAt = job.ScheduledAt,
        LeaseUntil = job.LeaseUntil ?? job.ScheduledAt,
    };
}
