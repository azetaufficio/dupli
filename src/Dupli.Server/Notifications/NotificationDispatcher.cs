using Dupli.Server.Configuration;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Notifications;

/// <summary>
/// Called by <c>AlertEvaluator</c> after it opens/resolves alerts. Two phases:
/// 1. Fan-out: every alert transition not yet distributed (<see cref="Alert.NotifiedAt"/>/<see cref="Alert.ResolvedNotifiedAt"/>)
///    becomes one <see cref="OperatorNotification"/> row per active user with at least one channel on for that
///    kind, in the same <c>SaveChangesAsync</c> call that marks the alert distributed (so a crash in between
///    cannot fan a transition out twice).
/// 2. Mail: every <c>Pending</c> row gets one <see cref="INotificationChannel.SendAsync"/> call; a failure is
///    retried on the next tick, up to <see cref="MaxEmailAttempts"/>, then marked <c>Failed</c>.
/// </summary>
public sealed class NotificationDispatcher(
    DupliDbContext db,
    INotificationChannel channel,
    IOptions<DupliServerOptions> options,
    ILogger<NotificationDispatcher> logger)
{
    private const int MaxEmailAttempts = 5;

    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        await FanOutAsync(now, ct);
        await SendPendingEmailAsync(now, ct);
    }

    private async Task FanOutAsync(DateTimeOffset now, CancellationToken ct)
    {
        var toNotify = await db.Alerts
            .Where(a => a.NotifiedAt == null || (a.ResolvedAt != null && a.ResolvedNotifiedAt == null))
            .OrderBy(a => a.OpenedAt)
            .ToListAsync(ct);
        if (toNotify.Count == 0)
            return;

        var users = await db.OperatorUsers.AsNoTracking().Where(u => u.DisabledAt == null).ToListAsync(ct);
        var preferencesByUser = (await db.NotificationPreferences.AsNoTracking().ToListAsync(ct)).ToLookup(p => p.UserId);
        var publicUrl = options.Value.PublicUrl?.TrimEnd('/');

        foreach (var alert in toNotify)
        {
            // An alert that opened and cleared before its first notification is only reported once, as resolved
            // (matches the pre-per-user behaviour): NotifiedAt and ResolvedNotifiedAt are then set together below.
            var resolved = alert.ResolvedAt is not null;
            var evt = resolved ? NotificationEvent.Resolved : NotificationEvent.Opened;
            var (subject, body) = BuildContent(alert, evt, publicUrl);

            foreach (var user in users)
            {
                var preference = preferencesByUser[user.Id].SingleOrDefault(p => p.Kind == alert.Kind);
                var (defaultEmail, defaultInApp) = NotificationDefaults.For(user.Role, alert.Kind);
                var email = preference?.Email ?? defaultEmail;
                var inApp = preference?.InApp ?? defaultInApp;
                if (!email && !inApp)
                    continue;

                db.Notifications.Add(new OperatorNotification
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    AlertId = alert.Id,
                    Kind = alert.Kind,
                    Event = evt,
                    Subject = subject,
                    Body = body,
                    AgentId = alert.AgentId,
                    PolicyId = alert.PolicyId,
                    CreatedAt = now,
                    InApp = inApp,
                    EmailStatus = email ? EmailStatus.Pending : EmailStatus.None,
                });
            }

            alert.NotifiedAt ??= now;
            if (resolved)
                alert.ResolvedNotifiedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SendPendingEmailAsync(DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.Notifications.Where(n => n.EmailStatus == EmailStatus.Pending).OrderBy(n => n.CreatedAt).ToListAsync(ct);
        if (pending.Count == 0)
            return;

        if (!channel.IsConfigured)
        {
            logger.LogWarning("Mail channel not configured: {Count} pending notification(s) marked Failed", pending.Count);
            foreach (var n in pending)
            {
                n.EmailStatus = EmailStatus.Failed;
                n.EmailError = "mail channel not configured";
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        var emailByUser = await db.OperatorUsers.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        foreach (var n in pending)
        {
            if (!emailByUser.TryGetValue(n.UserId, out var email))
            {
                n.EmailStatus = EmailStatus.Failed;
                n.EmailError = "user no longer exists";
                continue;
            }

            n.EmailAttempts++;
            try
            {
                await channel.SendAsync(new Notification(n.Subject, n.Body), [email], ct);
                n.EmailStatus = EmailStatus.Sent;
                n.EmailSentAt = now;
                n.EmailError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                n.EmailError = ex.Message;
                if (n.EmailAttempts >= MaxEmailAttempts)
                {
                    n.EmailStatus = EmailStatus.Failed;
                    logger.LogError(ex, "Notification {Id} mail failed permanently after {Attempts} attempts", n.Id, n.EmailAttempts);
                }
                else
                {
                    logger.LogWarning(ex, "Notification {Id} mail attempt {Attempt} failed; retrying", n.Id, n.EmailAttempts);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static (string Subject, string Body) BuildContent(Alert alert, NotificationEvent evt, string? publicUrl)
    {
        var link = alert.AgentId is { } agentId && !string.IsNullOrWhiteSpace(publicUrl) ? $"\n\n{publicUrl}/agents/{agentId}" : "";
        return evt == NotificationEvent.Resolved
            ? ($"[Dupli] RESOLVED {alert.Kind}: {alert.SubjectKey}", $"{alert.Message}\n\nResolved at {alert.ResolvedAt:u}.{link}")
            : ($"[Dupli] {alert.Kind}: {alert.SubjectKey}", $"{alert.Message}\n\nOpened at {alert.OpenedAt:u}.{link}");
    }
}
