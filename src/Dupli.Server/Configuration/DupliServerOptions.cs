namespace Dupli.Server.Configuration;

/// <summary>Bound from the <c>Dupli</c> configuration section.</summary>
public sealed class DupliServerOptions
{
    public const string Section = "Dupli";

    /// <summary>Public base URL agents use (e.g. https://backup.example.com). Defaults to the request host.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Directory where mirrored tool binaries (restic) are cached.</summary>
    public string ToolMirrorPath { get; set; } = "/var/lib/dupli/tools";

    public bool RunBackgroundServices { get; set; } = true;

    public AgentOptions Agents { get; set; } = new();
    public JobOptions Jobs { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public ReleaseOptions Releases { get; set; } = new();
    public RateLimitOptions RateLimiting { get; set; } = new();
    public RestoreOptions Restore { get; set; } = new();
    public NotificationsOptions Notifications { get; set; } = new();
}

/// <summary>Retention of the per-user notification feed (<c>notification</c> table). Not to be confused with the
/// top-level <c>Notifications</c> configuration section, which selects and configures the e-mail channel.</summary>
public sealed class NotificationsOptions
{
    public int RetentionDays { get; set; } = 90;
}

/// <summary>Server-side, read-only access to agent repositories (snapshot list and browse).</summary>
public sealed class RestoreOptions
{
    /// <summary>Explicit restic executable (dev/tests). When empty, the current restic release for the server platform
    /// is installed from the release mirror.</summary>
    public string? ResticPath { get; set; }

    /// <summary>restic cache (per agent sub-directory). Defaults to <c>cache</c> next to <see cref="DupliServerOptions.ToolMirrorPath"/>.</summary>
    public string? CachePath { get; set; }

    /// <summary>restic retries an unreachable S3 endpoint for minutes: cut a listing short instead.</summary>
    public TimeSpan ListingTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan SnapshotCacheDuration { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxConcurrentListings { get; set; } = 4;

    /// <summary>S3 endpoints the server reaches through a different URL than the agents (private endpoint, or a
    /// container network name in the dev stack).</summary>
    public List<EndpointOverride> EndpointOverrides { get; set; } = [];
}

public sealed class EndpointOverride
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>
/// Per client IP fixed window on the anonymous endpoints exposed to the internet (agent register/token, operator
/// login). The IP is the one reported by the reverse proxy (X-Forwarded-For).
/// </summary>
public sealed class RateLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Requests allowed per IP and window. Agents refresh their token every ~15 minutes, so the
    /// default leaves room for a few hundred agents behind one NAT address.</summary>
    public int PermitLimit { get; set; } = 30;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
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

    /// <summary>An active agent stuck off the version DesiredVersionResolver wants for this long raises AgentOutdated.</summary>
    public TimeSpan AgentOutdatedAfter { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Automation access to the admin API (scripts, CI). Operators use the web UI login instead.</summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Required value of the <c>X-Dupli-Admin-Key</c> header, also accepted by the <c>/admin</c> break-glass page.
    /// Key access and <c>/admin</c> disabled when empty.
    /// </summary>
    public string? ApiKey { get; set; }
}

public enum AuthMode
{
    /// <summary>No interactive login: the web UI cannot be used, only the admin key.</summary>
    None,

    /// <summary>Operators sign in with Microsoft Entra ID (OIDC, server-side BFF), in every environment.</summary>
    EntraId,
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.None;
    public EntraIdOptions EntraId { get; set; } = new();

    /// <summary>Idle lifetime of the operator session cookie (sliding).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// While no operator user exists, only this email (matched on the <c>email</c> or <c>preferred_username</c>
    /// claim) may sign in, and becomes the first <c>Owner</c>. Required in EntraId mode until then.
    /// </summary>
    public string? BootstrapOwnerEmail { get; set; }
}

/// <summary>Where mirrored release sources may come from and where "import from GitHub" looks.</summary>
public sealed class ReleaseOptions
{
    /// <summary>
    /// Dev/test only: when true, a release <c>sourceUrl</c> may be <c>http://</c> or <c>file://</c> (still
    /// sha256-verified). Production keeps https only.
    /// </summary>
    public bool AllowInsecureSources { get; set; }

    /// <summary><c>owner/repo</c> whose GitHub Releases carry agent build assets.</summary>
    public string GitHubRepository { get; set; } = "azetaufficio/dupli";
}

public sealed class EntraIdOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
}
