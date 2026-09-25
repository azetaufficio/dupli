using Cronos;
using Dupli.Contracts.Jobs;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Background;

/// <summary>
/// Creates Pending jobs when a cron occurrence is due: backups per policy, Retention,
/// RepositoryCheck and RestoreTest per agent. Missed occurrences collapse into one job (only the latest counts),
/// and the pending-job unique index keeps it to one per policy.
/// </summary>
public sealed class JobScheduler(
    DupliDbContext db,
    JobService jobs,
    IOptions<DupliServerOptions> options,
    TimeProvider time,
    ILogger<JobScheduler> logger) : IPeriodicTask
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await SchedulePoliciesAsync(now, cancellationToken);
        await ScheduleMaintenanceAsync(now, cancellationToken);
    }

    private async Task SchedulePoliciesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var policies = await db.Policies
            .Include(p => p.Sources)
            .Where(p => p.Enabled && db.Agents.Any(a => a.Id == p.AgentId && a.Status == AgentStatus.Active))
            .ToListAsync(ct);

        foreach (var policy in policies)
        {
            var from = policy.LastScheduledFor ?? policy.UpdatedAt;
            if (LatestDue(policy.Cron, policy.TimeZone, from, now) is not { } due)
                continue;

            policy.LastScheduledFor = due;
            await jobs.CreateBackupJobAsync(policy, JobTrigger.Schedule, due, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ScheduleMaintenanceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var m = options.Value.Maintenance;
        var agents = await db.Agents.Where(a => a.Status == AgentStatus.Active).ToListAsync(ct);
        foreach (var agent in agents)
        {
            var baseline = agent.EnrolledAt ?? agent.CreatedAt;

            if (LatestDue(m.RetentionCron, m.TimeZone, agent.LastRetentionScheduledFor ?? baseline, now) is { } retention)
            {
                agent.LastRetentionScheduledFor = retention;
                await jobs.CreateSystemJobAsync(agent.Id, JobType.Retention, JobTrigger.System, retention, ct);
                await db.SaveChangesAsync(ct);
            }

            if (LatestDue(m.CheckCron, m.TimeZone, agent.LastCheckScheduledFor ?? baseline, now) is { } check)
            {
                agent.LastCheckScheduledFor = check;
                await jobs.CreateSystemJobAsync(agent.Id, JobType.RepositoryCheck, JobTrigger.System, check, ct);
                await db.SaveChangesAsync(ct);
            }

            if (LatestDue(m.RestoreTestCron, m.TimeZone, agent.LastRestoreTestScheduledFor ?? baseline, now) is { } restoreTest)
            {
                agent.LastRestoreTestScheduledFor = restoreTest;
                await jobs.CreateSystemJobAsync(agent.Id, JobType.RestoreTest, JobTrigger.System, restoreTest, ct);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>Latest occurrence in (<paramref name="after"/>, <paramref name="now"/>], or null.</summary>
    internal DateTimeOffset? LatestDue(string cron, string timeZone, DateTimeOffset after, DateTimeOffset now)
    {
        CronExpression expression;
        TimeZoneInfo zone;
        try
        {
            expression = CronExpression.Parse(cron);
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException)
        {
            logger.LogError(ex, "Invalid schedule {Cron} ({TimeZone})", cron, timeZone);
            return null;
        }

        DateTime? latest = null;
        foreach (var occurrence in expression.GetOccurrences(after.UtcDateTime, now.UtcDateTime, zone, fromInclusive: false, toInclusive: true))
            latest = occurrence;
        return latest is { } l ? new DateTimeOffset(l, TimeSpan.Zero) : null;
    }
}

/// <summary>Also purges the notification feed: <see cref="JobService.SweepAsync"/> and this share a schedule,
/// there is no dedicated worker for either.</summary>
public sealed class JobSweeper(JobService jobs, DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time) : IPeriodicTask
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await jobs.SweepAsync(cancellationToken);
        await SweepNotificationsAsync(cancellationToken);
    }

    private async Task SweepNotificationsAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.Value.Notifications.RetentionDays);
        var stale = await db.Notifications.Where(n => n.CreatedAt < cutoff).ToListAsync(ct);
        if (stale.Count == 0)
            return;
        db.Notifications.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }
}
