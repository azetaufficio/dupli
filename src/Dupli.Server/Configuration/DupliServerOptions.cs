namespace Dupli.Server.Configuration;

/// <summary>Bound from the <c>Dupli</c> configuration section.</summary>
public sealed class DupliServerOptions
{
    public const string Section = "Dupli";

    /// <summary>Public base URL agents use (e.g. https://backup.example.com). Defaults to the request host.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Directory holding the Data Protection key ring. Must be outside the database volume.</summary>
    public string DataProtectionKeysPath { get; set; } = "/var/lib/dupli/keys";

    /// <summary>Directory where mirrored tool binaries (restic) are cached.</summary>
    public string ToolMirrorPath { get; set; } = "/var/lib/dupli/tools";

    public bool RunBackgroundServices { get; set; } = true;

    public AgentOptions Agents { get; set; } = new();
    public JobOptions Jobs { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
}

public sealed class AgentOptions
{
    public int PollIntervalSeconds { get; set; } = 30;
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan EnrollmentTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Base64 HMAC key (≥ 32 bytes) signing agent JWTs. When empty a random key is generated at startup:
    /// tokens then die with the process and agents transparently re-authenticate.
    /// </summary>
    public string? SigningKey { get; set; }
}

public sealed class JobOptions
{
    /// <summary>A Pending job not picked up within this window becomes Missed.</summary>
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The agent renews the lease with started/progress calls; a lapsed lease times the job out.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Hard cap on a running job, regardless of lease renewals.</summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class MaintenanceOptions
{
    public string RetentionCron { get; set; } = "0 3 * * 0";
    public string CheckCron { get; set; } = "0 5 * * 3";
    public string TimeZone { get; set; } = "Europe/Rome";
    public int CheckReadDataSubsetPercent { get; set; } = 5;
}

public sealed class AlertOptions
{
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>An enabled policy without a successful run for this long raises BackupTooOld.</summary>
    public TimeSpan BackupMaxAge { get; set; } = TimeSpan.FromHours(48);
}

/// <summary>M2 stop-gap for the admin API until the Entra ID BFF of M3.</summary>
public sealed class AdminOptions
{
    /// <summary>Required value of the <c>X-Dupli-Admin-Key</c> header. Admin API disabled when empty.</summary>
    public string? ApiKey { get; set; }
}
