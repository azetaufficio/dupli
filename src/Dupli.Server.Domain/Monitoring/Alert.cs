namespace Dupli.Server.Domain.Monitoring;

public enum AlertKind
{
    AgentOffline,
    BackupFailed,
    BackupMissed,
    BackupTooOld,
    RepositoryCheckFailed,
    RestoreTestFailed,
    AgentUpdateFailed,
}

/// <summary>
/// An open condition. At most one open alert per (Kind, SubjectKey): that is the dedup rule,
/// enforced by a partial unique index.
/// </summary>
public sealed class Alert
{
    public Guid Id { get; set; }
    public AlertKind Kind { get; set; }

    /// <summary><c>agent:{id}</c> or <c>policy:{id}</c>.</summary>
    public required string SubjectKey { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? PolicyId { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset? ResolvedNotifiedAt { get; set; }
}

public sealed record Notification(string Subject, string Body);

/// <summary>Email today; Teams/Telegram/webhook later.</summary>
public interface INotificationChannel
{
    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}

public sealed class AgentLog
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid? JobId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Level { get; set; }
    public required string Message { get; set; }
    public string? Exception { get; set; }

    /// <summary>jsonb object of string properties.</summary>
    public string? Properties { get; set; }
}
