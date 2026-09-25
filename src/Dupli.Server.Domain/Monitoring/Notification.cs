using Dupli.Server.Domain.Operators;

namespace Dupli.Server.Domain.Monitoring;

/// <summary>Whether an <see cref="OperatorNotification"/> reports the alert opening or clearing.</summary>
public enum NotificationEvent
{
    Opened,
    Resolved,
}

public enum EmailStatus
{
    /// <summary>The e-mail channel is off for this row (in-app only).</summary>
    None,
    Pending,
    Sent,
    Failed,
}

/// <summary>Per-user, per-alert-kind opt-in to e-mail and/or in-app notifications. Absent = <see cref="NotificationDefaults"/>.</summary>
public sealed class NotificationPreference
{
    public Guid UserId { get; set; }
    public AlertKind Kind { get; set; }
    public bool Email { get; set; }
    public bool InApp { get; set; }
}

/// <summary>
/// One row per (alert transition, recipient): fanned out by <c>NotificationDispatcher</c> from an
/// <see cref="Alert"/> opening or resolving, for every active user with at least one channel on for that kind.
/// </summary>
public sealed class OperatorNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AlertId { get; set; }
    public AlertKind Kind { get; set; }
    public NotificationEvent Event { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? PolicyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool InApp { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    public EmailStatus EmailStatus { get; set; }
    public int EmailAttempts { get; set; }
    public DateTimeOffset? EmailSentAt { get; set; }
    public string? EmailError { get; set; }
}

/// <summary>
/// Applied when a user has not set an explicit <see cref="NotificationPreference"/> for a kind: in-app for
/// everyone, e-mail for Owner and Operator (Viewer stays quiet by default).
/// </summary>
public static class NotificationDefaults
{
    public static (bool Email, bool InApp) For(OperatorRole role, AlertKind kind) =>
        (role != OperatorRole.Viewer, true);
}
