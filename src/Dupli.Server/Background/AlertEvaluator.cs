using Dupli.Contracts.Jobs;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Background;

/// <summary>
/// Recomputes every alert condition each tick (offline detection included), opens alerts for new
/// conditions, resolves the ones that cleared, and notifies once per transition. Unsent notifications
/// are retried on the next tick.
/// </summary>
public sealed class AlertEvaluator(
    DupliDbContext db,
    INotificationChannel channel,
    IOptions<DupliServerOptions> options,
    TimeProvider time,
    ILogger<AlertEvaluator> logger) : IPeriodicTask
{
    private sealed record Condition(AlertKind Kind, string SubjectKey, Guid? AgentId, Guid? PolicyId, string Message);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var conditions = await EvaluateAsync(now, cancellationToken);
        var open = await db.Alerts.Where(a => a.ResolvedAt == null).ToListAsync(cancellationToken);

        foreach (var c in conditions.Where(c => !open.Any(a => a.Kind == c.Kind && a.SubjectKey == c.SubjectKey)))
        {
            var alert = new Alert
            {
                Id = Guid.NewGuid(),
                Kind = c.Kind,
                SubjectKey = c.SubjectKey,
                AgentId = c.AgentId,
                PolicyId = c.PolicyId,
                Message = c.Message,
                OpenedAt = now,
            };
            db.Alerts.Add(alert);
            open.Add(alert);
            logger.LogWarning("Alert opened: {Kind} {Subject}: {Message}", c.Kind, c.SubjectKey, c.Message);
        }

        foreach (var alert in open.Where(a => a.ResolvedAt is null && !conditions.Any(c => c.Kind == a.Kind && c.SubjectKey == a.SubjectKey)))
        {
            alert.ResolvedAt = now;
            logger.LogInformation("Alert resolved: {Kind} {Subject}", alert.Kind, alert.SubjectKey);
        }

        await db.SaveChangesAsync(cancellationToken);
        await NotifyAsync(now, cancellationToken);
    }

    private async Task NotifyAsync(DateTimeOffset now, CancellationToken ct)
    {
        var toNotify = await db.Alerts
            .Where(a => a.NotifiedAt == null || (a.ResolvedAt != null && a.ResolvedNotifiedAt == null))
            .OrderBy(a => a.OpenedAt)
            .ToListAsync(ct);

        foreach (var alert in toNotify)
        {
            var resolved = alert.ResolvedAt is not null;
            try
            {
                // An alert that opened and cleared before its first notification is only reported once, as resolved.
                await channel.SendAsync(resolved
                    ? new Notification($"[Dupli] RESOLVED {alert.Kind}: {alert.SubjectKey}", $"{alert.Message}\n\nResolved at {alert.ResolvedAt:u}.")
                    : new Notification($"[Dupli] {alert.Kind}: {alert.SubjectKey}", $"{alert.Message}\n\nOpened at {alert.OpenedAt:u}."), ct);
                alert.NotifiedAt ??= now;
                if (resolved)
                    alert.ResolvedNotifiedAt = now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification for alert {AlertId} failed; retrying next tick", alert.Id);
                break;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Condition>> EvaluateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var conditions = new List<Condition>();

        var agents = await db.Agents.AsNoTracking().Where(a => a.Status == AgentStatus.Active).ToListAsync(ct);
        foreach (var agent in agents)
        {
            var lastSeen = agent.LastHeartbeatAt ?? agent.EnrolledAt ?? agent.CreatedAt;
            if (now - lastSeen > o.Agents.OfflineAfter)
                conditions.Add(new Condition(AlertKind.AgentOffline, $"agent:{agent.Id}", agent.Id, null,
                    $"Agent {agent.Name} ({agent.Hostname}) has not sent a heartbeat since {lastSeen:u}."));

            var lastCheck = await LatestFinishedAsync(agent.Id, null, JobType.RepositoryCheck, ct);
            if (lastCheck is { State: JobState.Failed or JobState.TimedOut })
                conditions.Add(new Condition(AlertKind.RepositoryCheckFailed, $"agent:{agent.Id}", agent.Id, null,
                    $"Repository check of {agent.Name} {lastCheck.State}: {lastCheck.Error}"));

            var lastRestoreTest = await LatestFinishedAsync(agent.Id, null, JobType.RestoreTest, ct);
            if (lastRestoreTest is { State: JobState.Failed or JobState.TimedOut })
                conditions.Add(new Condition(AlertKind.RestoreTestFailed, $"agent:{agent.Id}", agent.Id, null,
                    $"Restore test of {agent.Name} {lastRestoreTest.State}: {lastRestoreTest.Error}"));
        }

        var agentIds = agents.Select(a => a.Id).ToList();
        var agentNames = agents.ToDictionary(a => a.Id, a => a.Name);
        var policies = await db.Policies.AsNoTracking()
            .Where(p => p.Enabled && agentIds.Contains(p.AgentId))
            .ToListAsync(ct);

        foreach (var policy in policies)
        {
            var subject = $"policy:{policy.Id}";
            var label = $"{agentNames[policy.AgentId]} / {policy.Name}";

            var last = await LatestFinishedAsync(policy.AgentId, policy.Id, JobType.Backup, ct);
            switch (last?.State)
            {
                case JobState.Failed or JobState.TimedOut:
                    conditions.Add(new Condition(AlertKind.BackupFailed, subject, policy.AgentId, policy.Id,
                        $"Backup {label} {last.State}: {last.Error}"));
                    break;
                case JobState.Missed:
                    conditions.Add(new Condition(AlertKind.BackupMissed, subject, policy.AgentId, policy.Id,
                        $"Backup {label} scheduled at {last.ScheduledAt:u} was not executed."));
                    break;
            }

            var lastSuccess = await db.Runs.AsNoTracking()
                .Where(r => r.PolicyId == policy.Id && (r.Status == nameof(JobOutcome.Succeeded) || r.Status == nameof(JobOutcome.SucceededWithWarnings)))
                .MaxAsync(r => (DateTimeOffset?)r.CompletedAt, ct);
            var reference = lastSuccess ?? policy.CreatedAt;
            if (now - reference > o.Alerts.BackupMaxAge)
                conditions.Add(new Condition(AlertKind.BackupTooOld, subject, policy.AgentId, policy.Id,
                    lastSuccess is null
                        ? $"Backup {label} has never succeeded (policy created {policy.CreatedAt:u})."
                        : $"Last successful backup {label} is from {lastSuccess:u}."));
        }

        return conditions;
    }

    private Task<Job?> LatestFinishedAsync(Guid agentId, Guid? policyId, JobType type, CancellationToken ct) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.AgentId == agentId && j.PolicyId == policyId && j.Type == type && j.CompletedAt != null
                        && j.State != JobState.Cancelled)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefaultAsync(ct);
}
