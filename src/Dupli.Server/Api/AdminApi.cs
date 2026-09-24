using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cronos;
using Dupli.Contracts;
using Dupli.Contracts.Jobs;
using Dupli.Server.Agents;
using Dupli.Server.Auth;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Domain.Tools;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Security;
using Dupli.Server.Jobs;
using Dupli.Server.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Api;

// Dupli.Agent (namespace, from Agent.Core) would otherwise shadow the entity.
using Agent = Dupli.Server.Domain.Agents.Agent;

/// <summary>
/// Operator API under <c>/api/admin</c>: backend of the web UI (session cookie + antiforgery) and of
/// automation (admin key header).
/// </summary>
public static class AdminApi
{
    private const int MaxPageSize = 500;

    /// <summary>Named <see cref="IHttpClientFactory"/> client used to import agent releases from GitHub, so tests can fake it.</summary>
    public const string GitHubClientName = "dupli-github";

    /// <summary>Platforms the release workflow publishes (and the GitHub import looks for).</summary>
    private static readonly string[] PublishedAgentPlatforms = ["windows_amd64", "linux_amd64", "linux_arm64"];

    /// <summary>Accepted for manually registered releases: also macOS, for development hosts.</summary>
    private static readonly string[] AgentPlatforms = [.. PublishedAgentPlatforms, "darwin_arm64", "darwin_amd64"];
    private static readonly string[] AgentChannels = ["dev", "beta", "stable"];

    // Digits/dots, optional "-prerelease" suffix (dots allowed inside it too, e.g. 0.2.0-beta.1). No "+" or path characters.
    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+)*(-[A-Za-z0-9]+(\.[A-Za-z0-9]+)*)?$", RegexOptions.Compiled);

    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        // Reads need any operator role; every write is mapped on `operate` or `own` (enforced by a test).
        var admin = app.MapGroup("/api/admin").RequireAuthorization(AuthConstants.ViewerPolicy).RequireAntiforgeryForCookies();
        var operate = admin.MapGroup("").RequireAuthorization(AuthConstants.OperatorPolicy);
        var own = admin.MapGroup("").RequireAuthorization(AuthConstants.OwnerPolicy);

        admin.MapGet("/dashboard", DashboardAsync);
        admin.MapGet("/cron/preview", CronPreview);

        admin.MapGet("/storage-targets", async (DupliDbContext db, CancellationToken ct) =>
            (await db.StorageTargets.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto));
        own.MapPost("/storage-targets", CreateStorageTargetAsync);

        admin.MapGet("/agents", ListAgentsAsync);
        admin.MapGet("/agents/{id:guid}", GetAgentAsync);
        operate.MapPost("/agents", CreateAgentAsync);
        operate.MapPost("/agents/{id:guid}/enrollment-tokens", CreateEnrollmentTokenAsync);
        operate.MapPost("/agents/{id:guid}/disable", (Guid id, DupliDbContext db, CancellationToken ct) => SetStatusAsync(id, AgentStatus.Disabled, db, ct));
        operate.MapPost("/agents/{id:guid}/enable", EnableAsync);
        operate.MapPost("/agents/{id:guid}/jobs", RunSystemJobAsync);
        operate.MapPut("/agents/{id:guid}/update-settings", UpdateAgentSettingsAsync);

        admin.MapGet("/policies", async (DupliDbContext db, CancellationToken ct) =>
            (await db.Policies.AsNoTracking().Include(p => p.Sources).OrderBy(p => p.Name).ToListAsync(ct)).Select(ToDto));
        admin.MapGet("/agents/{id:guid}/policies", async (Guid id, DupliDbContext db, CancellationToken ct) =>
            (await db.Policies.AsNoTracking().Include(p => p.Sources).Where(p => p.AgentId == id).OrderBy(p => p.Name).ToListAsync(ct))
                .Select(ToDto));
        operate.MapPost("/agents/{id:guid}/policies", CreatePolicyAsync);
        admin.MapGet("/policies/{id:guid}", async (Guid id, DupliDbContext db, CancellationToken ct) =>
            ToDto(await LoadPolicyAsync(id, db, ct, tracking: false)));
        operate.MapPut("/policies/{id:guid}", UpdatePolicyAsync);
        operate.MapDelete("/policies/{id:guid}", DeletePolicyAsync);
        operate.MapPost("/policies/{id:guid}/run", RunPolicyAsync);

        admin.MapGet("/jobs", ListJobsAsync);
        admin.MapGet("/jobs/{id:guid}", async (Guid id, DupliDbContext db, CancellationToken ct) =>
            ToDto(await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("Job")));
        operate.MapPost("/jobs/{id:guid}/cancel", async (Guid id, JobService jobs, CancellationToken ct) =>
            ToDto(await jobs.RequestCancelAsync(id, ct)));

        admin.MapGet("/runs", ListRunsAsync);
        admin.MapGet("/logs", ListLogsAsync);
        admin.MapGet("/alerts", ListAlertsAsync);

        admin.MapGet("/releases", ListReleasesAsync);
        own.MapPost("/releases/restic", CreateReleaseAsync);
        own.MapPost("/releases/agent", CreateAgentReleaseAsync);
        own.MapPost("/releases/agent/import", ImportAgentReleaseAsync);
        own.MapPost("/releases/{id:guid}/make-current", MakeCurrentAsync);

        admin.MapRestoreApi();
        admin.MapConnectionsApi();
        app.MapUsersApi();
    }

    private static async Task<IResult> CreateStorageTargetAsync(CreateStorageTargetRequest request, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(request.Bucket))
            throw ApiException.BadRequest("Name, absolute endpoint URL and bucket are required");

        var target = new StorageTarget
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Endpoint = request.Endpoint.TrimEnd('/'),
            Bucket = request.Bucket.Trim(),
            Region = string.IsNullOrWhiteSpace(request.Region) ? null : request.Region.Trim(),
            CreatedAt = time.GetUtcNow(),
        };
        db.StorageTargets.Add(target);
        await SaveOrConflictAsync(db, "A storage target with this name already exists", ct);
        return Results.Created($"/api/admin/storage-targets/{target.Id}", ToDto(target));
    }

    private static async Task<IEnumerable<AgentDto>> ListAgentsAsync(
        DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time, DesiredVersionResolver resolver, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        var desired = await resolver.ResolveManyAsync(agents, ct); // one releases query for the whole list
        return agents.Select(a => ToDto(a, now, options.Value.Agents.OfflineAfter, desired[a.Id]));
    }

    private static async Task<AgentDto> GetAgentAsync(
        Guid id, DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time, DesiredVersionResolver resolver, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Agent");
        var desired = await resolver.ResolveAsync(agent, ct);
        return ToDto(agent, time.GetUtcNow(), options.Value.Agents.OfflineAfter, desired);
    }

    private static async Task<IResult> UpdateAgentSettingsAsync(
        Guid id, UpdateAgentSettingsRequest request, DupliDbContext db, CancellationToken ct)
    {
        if (!AgentChannels.Contains(request.Channel))
            throw ApiException.BadRequest("channel must be dev, beta or stable");

        var agent = await db.Agents.FindAsync([id], ct) ?? throw ApiException.NotFound("Agent");

        if (request.PinnedAgentVersion is { } pinnedAgent
            && !await db.Releases.AnyAsync(r => r.Product == ReleaseMirror.AgentProduct && r.Platform == agent.Platform && r.Version == pinnedAgent, ct))
            throw ApiException.BadRequest($"No agent release {pinnedAgent} for platform {agent.Platform}");

        if (request.PinnedResticVersion is { } pinnedRestic
            && !await db.Releases.AnyAsync(r => r.Product == ReleaseMirror.ResticProduct && r.Platform == agent.Platform && r.Version == pinnedRestic, ct))
            throw ApiException.BadRequest($"No restic release {pinnedRestic} for platform {agent.Platform}");

        agent.Channel = request.Channel;
        agent.PinnedAgentVersion = string.IsNullOrWhiteSpace(request.PinnedAgentVersion) ? null : request.PinnedAgentVersion;
        agent.PinnedResticVersion = string.IsNullOrWhiteSpace(request.PinnedResticVersion) ? null : request.PinnedResticVersion;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateAgentAsync(
        CreateAgentRequest request, DupliDbContext db, SecretProtector protector,
        IOptions<DupliServerOptions> options, TimeProvider time, DesiredVersionResolver resolver, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.StoragePrefix)
            || string.IsNullOrWhiteSpace(request.S3AccessKeyId) || string.IsNullOrWhiteSpace(request.S3SecretAccessKey))
            throw ApiException.BadRequest("Name, storage prefix and S3 credentials are required");
        if (!await db.StorageTargets.AnyAsync(s => s.Id == request.StorageTargetId, ct))
            throw ApiException.BadRequest("Unknown storage target");

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            StorageTargetId = request.StorageTargetId,
            StoragePrefix = request.StoragePrefix.Trim('/'),
            S3AccessKeyId = request.S3AccessKeyId.Trim(),
            S3SecretKeyProtected = protector.Protect(request.S3SecretAccessKey),
            RepositoryPasswordProtected = protector.Protect(
                string.IsNullOrEmpty(request.RepositoryPassword) ? SecretHashing.NewSecret() : request.RepositoryPassword),
            CreatedAt = time.GetUtcNow(),
        };
        db.Agents.Add(agent);
        await SaveOrConflictAsync(db, "Agent name or storage prefix already in use", ct);
        var desired = await resolver.ResolveAsync(agent, ct);
        return Results.Created($"/api/admin/agents/{agent.Id}", ToDto(agent, time.GetUtcNow(), options.Value.Agents.OfflineAfter, desired));
    }

    private static async Task<EnrollmentTokenDto> CreateEnrollmentTokenAsync(
        Guid id, EnrollmentService enrollment, IOptions<DupliServerOptions> options, TimeProvider time, CancellationToken ct)
    {
        var token = await enrollment.CreateEnrollmentTokenAsync(id, ct);
        return new EnrollmentTokenDto(token, time.GetUtcNow() + options.Value.Agents.EnrollmentTokenLifetime);
    }

    private static async Task<IResult> SetStatusAsync(Guid id, AgentStatus status, DupliDbContext db, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([id], ct) ?? throw ApiException.NotFound("Agent");
        agent.Status = status;
        agent.SecretHash = status == AgentStatus.Disabled ? null : agent.SecretHash;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Disabled → Pending: the agent must enroll again (new token), its secret having been revoked.</summary>
    private static async Task<IResult> EnableAsync(Guid id, DupliDbContext db, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([id], ct) ?? throw ApiException.NotFound("Agent");
        if (agent.Status != AgentStatus.Disabled)
            throw ApiException.Conflict($"Agent is {agent.Status}, not Disabled");
        agent.Status = AgentStatus.Pending;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RunSystemJobAsync(
        Guid id, RunSystemJobRequest request, DupliDbContext db, JobService jobs, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == id, ct))
            throw ApiException.NotFound("Agent");
        var job = await jobs.CreateSystemJobAsync(id, request.Type, JobTrigger.Manual, time.GetUtcNow(), ct)
            ?? throw ApiException.Conflict($"A {request.Type} job is already pending for this agent");
        return Results.Accepted($"/api/admin/jobs/{job.Id}", ToDto(job));
    }

    private static async Task<IResult> CreatePolicyAsync(Guid id, PolicyRequest request, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        PolicyValidator.Validate(request);
        if (!await db.Agents.AnyAsync(a => a.Id == id, ct))
            throw ApiException.NotFound("Agent");
        await PolicyValidator.ValidateConnectionsAsync(request, id, db, ct);

        var now = time.GetUtcNow();
        var policy = new BackupPolicy { Id = Guid.NewGuid(), AgentId = id, Name = "", Cron = "", CreatedAt = now };
        Apply(policy, request, now);
        db.Policies.Add(policy);
        await SaveOrConflictAsync(db, "A policy with this name already exists for the agent", ct);
        return Results.Created($"/api/admin/policies/{policy.Id}", ToDto(policy));
    }

    private static async Task<PolicyDto> UpdatePolicyAsync(Guid id, PolicyRequest request, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        PolicyValidator.Validate(request);
        var policy = await LoadPolicyAsync(id, db, ct, tracking: true);
        await PolicyValidator.ValidateConnectionsAsync(request, policy.AgentId, db, ct);
        db.Sources.RemoveRange(policy.Sources);
        policy.Sources.Clear();
        Apply(policy, request, time.GetUtcNow());
        await SaveOrConflictAsync(db, "A policy with this name already exists for the agent", ct);
        return ToDto(policy);
    }

    private static void Apply(BackupPolicy policy, PolicyRequest request, DateTimeOffset now)
    {
        policy.Name = request.Name.Trim();
        policy.Cron = request.Cron.Trim();
        policy.TimeZone = request.TimeZone;
        policy.Enabled = request.Enabled;
        policy.KeepDaily = request.Retention.KeepDaily;
        policy.KeepWeekly = request.Retention.KeepWeekly;
        policy.KeepMonthly = request.Retention.KeepMonthly;
        policy.UpdatedAt = now;
        policy.LastScheduledFor ??= now; // first occurrence is the next one, not a catch-up of the past
        policy.Sources.AddRange(request.Sources.Select(s => new BackupSource
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            SourceKey = s.SourceId,
            Type = PolicySpecBuilder.TypeOf(s),
            Spec = PolicySpecBuilder.Serialize(s),
        }));
    }

    private static async Task<IResult> DeletePolicyAsync(Guid id, DupliDbContext db, CancellationToken ct)
    {
        var policy = await LoadPolicyAsync(id, db, ct, tracking: true);
        db.Policies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RunPolicyAsync(Guid id, DupliDbContext db, JobService jobs, TimeProvider time, CancellationToken ct)
    {
        var policy = await LoadPolicyAsync(id, db, ct, tracking: false);
        var job = await jobs.CreateBackupJobAsync(policy, JobTrigger.Manual, time.GetUtcNow(), ct)
            ?? throw ApiException.Conflict("A backup job is already pending for this policy");
        return Results.Accepted($"/api/admin/jobs/{job.Id}", ToDto(job));
    }

    private static async Task<IEnumerable<JobDto>> ListJobsAsync(
        DupliDbContext db, CancellationToken ct, Guid? agentId = null, Guid? policyId = null, JobState? state = null,
        JobType? type = null, int limit = 100)
    {
        var query = db.Jobs.AsNoTracking();
        if (agentId is { } a) query = query.Where(j => j.AgentId == a);
        if (policyId is { } p) query = query.Where(j => j.PolicyId == p);
        if (state is { } s) query = query.Where(j => j.State == s);
        if (type is { } t) query = query.Where(j => j.Type == t);
        return (await query.OrderByDescending(j => j.CreatedAt).Take(Clamp(limit)).ToListAsync(ct)).Select(ToDto);
    }

    private static async Task<IEnumerable<RunDto>> ListRunsAsync(
        DupliDbContext db, CancellationToken ct, Guid? agentId = null, Guid? policyId = null, int limit = 100)
    {
        var query = db.Runs.AsNoTracking();
        if (agentId is { } a) query = query.Where(r => r.AgentId == a);
        if (policyId is { } p) query = query.Where(r => r.PolicyId == p);
        return (await query.OrderByDescending(r => r.CompletedAt).Take(Clamp(limit)).ToListAsync(ct)).Select(ToDto);
    }

    private static async Task<IEnumerable<LogDto>> ListLogsAsync(
        DupliDbContext db, CancellationToken ct, Guid? agentId = null, Guid? jobId = null, string? level = null, int limit = 200)
    {
        var query = db.Logs.AsNoTracking();
        if (agentId is { } a) query = query.Where(l => l.AgentId == a);
        if (jobId is { } j) query = query.Where(l => l.JobId == j);
        if (!string.IsNullOrEmpty(level)) query = query.Where(l => l.Level == level);
        return (await query.OrderByDescending(l => l.Timestamp).ThenByDescending(l => l.Id).Take(Clamp(limit)).ToListAsync(ct))
            .Select(l => new LogDto(l.Id, l.AgentId, l.JobId, l.Timestamp, l.Level, l.Message, l.Exception));
    }

    private static async Task<IEnumerable<AlertDto>> ListAlertsAsync(
        DupliDbContext db, CancellationToken ct, bool open = true, Guid? agentId = null, int limit = 200)
    {
        var query = db.Alerts.AsNoTracking();
        if (open) query = query.Where(a => a.ResolvedAt == null);
        if (agentId is { } id) query = query.Where(a => a.AgentId == id);
        return (await query.OrderByDescending(a => a.OpenedAt).Take(Clamp(limit)).ToListAsync(ct))
            .Select(a => new AlertDto(a.Id, a.Kind, a.SubjectKey, a.AgentId, a.PolicyId, a.Message, a.OpenedAt, a.ResolvedAt));
    }

    private static async Task<DashboardDto> DashboardAsync(
        DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time, DesiredVersionResolver resolver, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var offlineAfter = options.Value.Agents.OfflineAfter;
        var agentEntities = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        var desiredByAgent = await resolver.ResolveManyAsync(agentEntities, ct);
        var agents = agentEntities.Select(a => ToDto(a, now, offlineAfter, desiredByAgent[a.Id])).ToList();

        var running = (await db.Jobs.AsNoTracking()
                .Where(j => j.State == JobState.Assigned || j.State == JobState.Running)
                .Select(j => new { j.AgentId, j.Type })
                .ToListAsync(ct))
            .ToLookup(j => j.AgentId, j => j.Type);

        // Latest run per agent (any policy): the "last backup" status column.
        var lastRuns = (await db.Runs.AsNoTracking()
                .GroupBy(r => r.AgentId)
                .Select(g => g.OrderByDescending(r => r.CompletedAt).Select(r => new { r.AgentId, r.Status }).First())
                .ToListAsync(ct))
            .ToDictionary(r => r.AgentId, r => r.Status);

        var openAlerts = await db.Alerts.AsNoTracking().Where(a => a.ResolvedAt == null)
            .Select(a => new { a.AgentId, a.Kind }).ToListAsync(ct);
        var alertsByAgent = openAlerts.Where(a => a.AgentId != null).ToLookup(a => a.AgentId!.Value);

        var rows = agents.Select(a => new DashboardAgentDto(
            a,
            lastRuns.GetValueOrDefault(a.Id),
            running[a.Id].Select(t => t.ToString()).FirstOrDefault(),
            alertsByAgent[a.Id].Count())).ToList();

        var active = agents.Where(a => a.Status == AgentStatus.Active).ToList();
        return new DashboardDto(
            new DashboardCountersDto(
                Online: active.Count(a => a.Online),
                Offline: active.Count(a => !a.Online),
                Pending: agents.Count(a => a.Status == AgentStatus.Pending),
                BackupFailed: openAlerts.Where(a => a.Kind == AlertKind.BackupFailed).Select(a => a.AgentId).Distinct().Count(),
                BackupRunning: rows.Count(r => r.RunningJob == nameof(JobType.Backup)),
                OpenAlerts: openAlerts.Count),
            rows);
    }

    private static CronPreviewDto CronPreview(TimeProvider time, string cron, string timeZone = "Europe/Rome", int count = 5)
    {
        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(cron);
        }
        catch (CronFormatException ex)
        {
            return new CronPreviewDto(false, ex.Message, []);
        }
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
            return new CronPreviewDto(false, $"Unknown time zone '{timeZone}'", []);

        var next = expression.GetOccurrences(time.GetUtcNow().UtcDateTime, DateTime.MaxValue, zone, fromInclusive: false)
            .Take(Math.Clamp(count, 1, 20))
            .Select(o => new DateTimeOffset(o, TimeSpan.Zero))
            .ToList();
        return new CronPreviewDto(true, null, next);
    }

    private static async Task<IResult> CreateReleaseAsync(
        CreateReleaseRequest request, DupliDbContext db, TimeProvider time, IOptions<DupliServerOptions> options, CancellationToken ct)
    {
        if (request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit))
            throw ApiException.BadRequest("sha256 must be 64 hex characters");
        ValidateSourceUrl(request.SourceUrl, options.Value.Releases.AllowInsecureSources);

        var release = await CreateReleaseCoreAsync(
            db, time, ReleaseMirror.ResticProduct, request.Version, request.Platform, channel: null,
            request.SourceUrl, request.Sha256, request.MakeCurrent, ct);
        return Results.Created($"/api/tools/restic/{release.Version}/{release.Platform}", ToDto(release));
    }

    private static async Task<IResult> CreateAgentReleaseAsync(
        CreateAgentReleaseRequest request, DupliDbContext db, TimeProvider time, IOptions<DupliServerOptions> options, CancellationToken ct)
    {
        ValidateAgentReleaseFields(request.Version, request.Platform, request.Channel, request.Sha256);
        ValidateSourceUrl(request.SourceUrl, options.Value.Releases.AllowInsecureSources);

        var release = await CreateReleaseCoreAsync(
            db, time, ReleaseMirror.AgentProduct, request.Version, request.Platform, request.Channel,
            request.SourceUrl, request.Sha256, request.MakeCurrent, ct);
        return Results.Created($"/api/tools/agent/{release.Version}/{release.Platform}", ToDto(release));
    }

    /// <summary>
    /// Builds the GitHub Releases asset URLs for a tag (<c>dupli-agent_&lt;ver&gt;_&lt;platform&gt;[.exe]</c> +
    /// <c>.sha256</c>) and registers whatever platform assets exist; a missing platform (404) is skipped.
    /// </summary>
    private static async Task<IResult> ImportAgentReleaseAsync(
        ImportAgentReleaseRequest request, DupliDbContext db, TimeProvider time,
        IHttpClientFactory httpClientFactory, IOptions<DupliServerOptions> options, CancellationToken ct)
    {
        if (!VersionPattern.IsMatch(request.Version))
            throw ApiException.BadRequest("version must look like a semantic version (digits/dots, optional -prerelease)");
        if (!AgentChannels.Contains(request.Channel))
            throw ApiException.BadRequest("channel must be dev, beta or stable");

        var repo = options.Value.Releases.GitHubRepository;
        var client = httpClientFactory.CreateClient(GitHubClientName);
        var registered = new List<ReleaseDto>();

        foreach (var platform in PublishedAgentPlatforms)
        {
            var fileName = platform == "windows_amd64"
                ? $"dupli-agent_{request.Version}_{platform}.exe"
                : $"dupli-agent_{request.Version}_{platform}";
            var assetUrl = $"https://github.com/{repo}/releases/download/v{request.Version}/{fileName}";

            using var shaResponse = await client.GetAsync($"{assetUrl}.sha256", ct);
            if (shaResponse.StatusCode == HttpStatusCode.NotFound)
                continue;
            shaResponse.EnsureSuccessStatusCode();

            // sha256sum format: "<hex>  <filename>". We only need the first token.
            var shaContent = await shaResponse.Content.ReadAsStringAsync(ct);
            var sha256 = shaContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"Unexpected .sha256 content for {fileName}: '{shaContent}'");

            var release = await CreateReleaseCoreAsync(
                db, time, ReleaseMirror.AgentProduct, request.Version, platform, request.Channel,
                assetUrl, sha256, request.MakeCurrent, ct);
            registered.Add(ToDto(release));
        }

        if (registered.Count == 0)
            throw ApiException.BadRequest($"No agent release assets found on GitHub for version {request.Version} (tag v{request.Version})");

        return Results.Ok(registered);
    }

    private static async Task<IResult> MakeCurrentAsync(Guid id, DupliDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var release = await db.Releases.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Release");
        if (!release.IsCurrent)
        {
            await db.Releases
                .Where(r => r.Product == release.Product && r.Platform == release.Platform && r.Channel == release.Channel && r.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct);
            release.IsCurrent = true;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IEnumerable<ReleaseDto>> ListReleasesAsync(DupliDbContext db, CancellationToken ct, string product = ReleaseMirror.ResticProduct) =>
        (await db.Releases.AsNoTracking().Where(r => r.Product == product).OrderByDescending(r => r.CreatedAt).ToListAsync(ct))
            .Select(ToDto);

    /// <summary>Shared insert path for restic and agent releases: one transaction so a conflict never leaves the
    /// product/platform/channel without a current release.</summary>
    private static async Task<SoftwareRelease> CreateReleaseCoreAsync(
        DupliDbContext db, TimeProvider time, string product, string version, string platform, string? channel,
        string sourceUrl, string sha256, bool makeCurrent, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Releases.AnyAsync(r => r.Product == product && r.Version == version && r.Platform == platform, ct))
            throw ApiException.Conflict("This release already exists");

        if (makeCurrent)
        {
            await db.Releases
                .Where(r => r.Product == product && r.Platform == platform && r.Channel == channel && r.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct);
        }

        var release = new SoftwareRelease
        {
            Id = Guid.NewGuid(),
            Product = product,
            Version = version,
            Platform = platform,
            Channel = channel,
            SourceUrl = sourceUrl,
            Sha256 = sha256.ToLowerInvariant(),
            IsCurrent = makeCurrent,
            CreatedAt = time.GetUtcNow(),
        };
        db.Releases.Add(release);
        await SaveOrConflictAsync(db, "This release already exists", ct);
        await tx.CommitAsync(ct);
        return release;
    }

    private static void ValidateSourceUrl(string sourceUrl, bool allowInsecure)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url))
            throw ApiException.BadRequest("sourceUrl must be an absolute URL");
        var ok = allowInsecure ? url.Scheme is "https" or "http" or "file" : url.Scheme == Uri.UriSchemeHttps;
        if (!ok)
            throw ApiException.BadRequest(allowInsecure
                ? "sourceUrl must use https, http or file"
                : "sourceUrl must be an absolute https URL");
    }

    private static void ValidateAgentReleaseFields(string version, string platform, string channel, string sha256)
    {
        if (!VersionPattern.IsMatch(version))
            throw ApiException.BadRequest("version must look like a semantic version (digits/dots, optional -prerelease)");
        if (!AgentPlatforms.Contains(platform))
            throw ApiException.BadRequest($"platform must be one of {string.Join(", ", AgentPlatforms)}");
        if (!AgentChannels.Contains(channel))
            throw ApiException.BadRequest("channel must be dev, beta or stable");
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw ApiException.BadRequest("sha256 must be 64 hex characters");
    }

    private static async Task<BackupPolicy> LoadPolicyAsync(Guid id, DupliDbContext db, CancellationToken ct, bool tracking)
    {
        var query = db.Policies.Include(p => p.Sources).Where(p => p.Id == id);
        return await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Policy");
    }

    internal static async Task SaveOrConflictAsync(DupliDbContext db, string conflictMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw ApiException.Conflict(conflictMessage);
        }
    }

    private static int Clamp(int limit) => Math.Clamp(limit, 1, MaxPageSize);

    private static StorageTargetDto ToDto(StorageTarget s) => new(s.Id, s.Name, s.Endpoint, s.Bucket, s.Region);

    private static AgentDto ToDto(Agent a, DateTimeOffset now, TimeSpan offlineAfter, (SoftwareRelease? Agent, SoftwareRelease? Restic) desired) => new(
        a.Id, a.Name, a.Status, a.IsOnline(now, offlineAfter), a.Hostname, a.OsVersion, a.Version, a.ResticVersion,
        a.LastHeartbeatAt, a.LastBackupAt, a.FreeDiskSpace, a.StorageTargetId, a.StoragePrefix, a.CreatedAt, a.EnrolledAt,
        a.Platform, a.Channel, a.PinnedAgentVersion, a.PinnedResticVersion, a.LauncherManaged,
        desired.Agent?.Version, desired.Restic?.Version,
        a.LastUpdateVersion, a.LastUpdateOutcome, a.LastUpdateError, a.LastUpdateAt, a.ResticUpdateError);

    private static ReleaseDto ToDto(SoftwareRelease r) =>
        new(r.Id, r.Product, r.Version, r.Platform, r.Channel, r.SourceUrl, r.Sha256, r.IsCurrent, r.CreatedAt);

    private static PolicyDto ToDto(BackupPolicy p) => new(
        p.Id, p.AgentId, p.Name, p.Cron, p.TimeZone, p.Enabled, PolicySpecBuilder.Retention(p),
        p.Sources.OrderBy(s => s.SourceKey, StringComparer.Ordinal).Select(PolicySpecBuilder.ToDto).ToList(),
        NextRun(p), p.LastScheduledFor);

    private static DateTimeOffset? NextRun(BackupPolicy p)
    {
        if (!p.Enabled || !TimeZoneInfo.TryFindSystemTimeZoneById(p.TimeZone, out var zone))
            return null;
        var next = CronExpression.Parse(p.Cron).GetNextOccurrence(DateTime.UtcNow, zone);
        return next is { } n ? new DateTimeOffset(n, TimeSpan.Zero) : null;
    }

    internal static JobDto ToDto(Job j) => new(
        j.Id, j.AgentId, j.PolicyId, j.Type, j.Trigger, j.State, j.CreatedAt, j.ScheduledAt, j.ExpiresAt,
        j.StartedAt, j.CompletedAt, j.CancelRequested, j.Error,
        j.ResultItems is null ? [] : JsonSerializer.Deserialize<List<JobItemResultDto>>(j.ResultItems, DupliJson.Options) ?? []);

    private static RunDto ToDto(BackupRun r) => new(
        r.Id, r.JobId, r.PolicyId, r.AgentId, r.StartedAt, r.CompletedAt, r.Status, r.BytesProcessed, r.BytesAdded,
        JsonSerializer.Deserialize<List<JobItemResultDto>>(r.Items, DupliJson.Options) ?? [], r.ErrorMessage);
}
