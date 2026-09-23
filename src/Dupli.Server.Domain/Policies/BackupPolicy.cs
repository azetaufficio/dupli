namespace Dupli.Server.Domain.Policies;

public enum BackupSourceType
{
    Directory,
    PostgreSql,
}

public sealed class BackupPolicy
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public required string Name { get; set; }

    /// <summary>5-field cron, evaluated in <see cref="TimeZone"/>.</summary>
    public required string Cron { get; set; }
    public string TimeZone { get; set; } = "Europe/Rome";
    public bool Enabled { get; set; } = true;

    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 12;

    /// <summary>Last cron occurrence a job was created for (scheduler watermark).</summary>
    public DateTimeOffset? LastScheduledFor { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<BackupSource> Sources { get; set; } = [];
}

public sealed class BackupSource
{
    public Guid Id { get; set; }
    public Guid PolicyId { get; set; }

    /// <summary>Stable identifier used in the restic <c>source=</c> tag.</summary>
    public required string SourceKey { get; set; }
    public BackupSourceType Type { get; set; }

    /// <summary>Serialized <c>BackupSourceDto</c> (jsonb). Never contains secret values.</summary>
    public required string Spec { get; set; }
}

public sealed class BackupRun
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid AgentId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary><c>JobOutcome</c> name.</summary>
    public required string Status { get; set; }
    public long BytesProcessed { get; set; }
    public long BytesAdded { get; set; }

    /// <summary>Serialized item results (jsonb): snapshot id per source item.</summary>
    public required string Items { get; set; }
    public string? ErrorMessage { get; set; }
}
