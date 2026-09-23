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
    public AuthOptions Auth { get; set; } = new();
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

    /// <summary>Expiry of an operator "restart agent" request.</summary>
    public TimeSpan RestartExpiry { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class MaintenanceOptions
{
    public string RetentionCron { get; set; } = "0 3 * * 0";
    public string CheckCron { get; set; } = "0 5 * * 3";
    public string TimeZone { get; set; } = "Europe/Rome";
    public int CheckReadDataSubsetPercent { get; set; } = 5;
    public string RestoreTestCron { get; set; } = "0 7 * * 6";
    public int RestoreTestSampleFiles { get; set; } = 20;
}

public sealed class AlertOptions
{
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>An enabled policy without a successful run for this long raises BackupTooOld.</summary>
    public TimeSpan BackupMaxAge { get; set; } = TimeSpan.FromHours(48);
}

/// <summary>Automation access to the admin API (scripts, CI). Operators use the web UI login instead.</summary>
public sealed class AdminOptions
{
    /// <summary>Required value of the <c>X-Dupli-Admin-Key</c> header. Key access disabled when empty.</summary>
    public string? ApiKey { get; set; }
}

public enum AuthMode
{
    /// <summary>No interactive login: the web UI cannot be used, only the admin key.</summary>
    None,

    /// <summary>Operators sign in with Microsoft Entra ID (OIDC, server-side BFF).</summary>
    EntraId,

    /// <summary>Local development only: <c>/bff/login</c> signs in a fixed user without a password.</summary>
    Development,
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.None;
    public EntraIdOptions EntraId { get; set; } = new();

    /// <summary>Idle lifetime of the operator session cookie (sliding).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    public string DevelopmentUser { get; set; } = "developer";
}

public sealed class EntraIdOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>When set, only users with this app role (the <c>roles</c> claim) may use the UI.</summary>
    public string? RequiredRole { get; set; }
}
