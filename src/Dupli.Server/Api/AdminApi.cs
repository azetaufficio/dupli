using System.Text.Json;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Api;

/// <summary>
/// Operator API under <c>/api/admin</c>: backend of the web UI (session cookie + antiforgery) and of
/// automation (admin key header).
/// </summary>
public static class AdminApi
{
    private const int MaxPageSize = 500;

    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(AuthConstants.AdminPolicy).RequireAntiforgeryForCookies();

        admin.MapGet("/dashboard", DashboardAsync);
        admin.MapGet("/cron/preview", CronPreview);

        admin.MapGet("/storage-targets", async (DupliDbContext db, CancellationToken ct) =>
            (await db.StorageTargets.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto));
        admin.MapPost("/storage-targets", CreateStorageTargetAsync);

        admin.MapGet("/agents", ListAgentsAsync);
        admin.MapGet("/agents/{id:guid}", GetAgentAsync);
        admin.MapPost("/agents", CreateAgentAsync);
        admin.MapPost("/agents/{id:guid}/enrollment-tokens", CreateEnrollmentTokenAsync);
        admin.MapPost("/agents/{id:guid}/disable", (Guid id, DupliDbContext db, CancellationToken ct) => SetStatusAsync(id, AgentStatus.Disabled, db, ct));
        admin.MapPost("/agents/{id:guid}/enable", EnableAsync);
        admin.MapPost("/agents/{id:guid}/jobs", RunSystemJobAsync);

        admin.MapGet("/policies", async (DupliDbContext db, CancellationToken ct) =>
            (await db.Policies.AsNoTracking().Include(p => p.Sources).OrderBy(p => p.Name).ToListAsync(ct)).Select(ToDto));
        admin.MapGet("/agents/{id:guid}/policies", async (Guid id, DupliDbContext db, CancellationToken ct) =>
            (await db.Policies.AsNoTracking().Include(p => p.Sources).Where(p => p.AgentId == id).OrderBy(p => p.Name).ToListAsync(ct))
                .Select(ToDto));
        admin.MapPost("/agents/{id:guid}/policies", CreatePolicyAsync);
        admin.MapGet("/policies/{id:guid}", async (Guid id, DupliDbContext db, CancellationToken ct) =>
            ToDto(await LoadPolicyAsync(id, db, ct, tracking: false)));
        admin.MapPut("/policies/{id:guid}", UpdatePolicyAsync);
        admin.MapDelete("/policies/{id:guid}", DeletePolicyAsync);
        admin.MapPost("/policies/{id:guid}/run", RunPolicyAsync);

        admin.MapGet("/jobs", ListJobsAsync);
        admin.MapGet("/jobs/{id:guid}", async (Guid id, DupliDbContext db, CancellationToken ct) =>
            ToDto(await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("Job")));
        admin.MapPost("/jobs/{id:guid}/cancel", async (Guid id, JobService jobs, CancellationToken ct) =>
            ToDto(await jobs.RequestCancelAsync(id, ct)));

        admin.MapGet("/runs", ListRunsAsync);
        admin.MapGet("/logs", ListLogsAsync);
        admin.MapGet("/alerts", ListAlertsAsync);
        admin.MapPost("/releases/restic", CreateReleaseAsync);
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
        DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return (await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct))
            .Select(a => ToDto(a, now, options.Value.Agents.OfflineAfter));
    }

    private static async Task<AgentDto> GetAgentAsync(
        Guid id, DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Agent");
        return ToDto(agent, time.GetUtcNow(), options.Value.Agents.OfflineAfter);
    }

    private static async Task<IResult> CreateAgentAsync(
        CreateAgentRequest request, DupliDbContext db, SecretProtector protector,
        IOptions<DupliServerOptions> options, TimeProvider time, CancellationToken ct)
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
        return Results.Created($"/api/admin/agents/{agent.Id}", ToDto(agent, time.GetUtcNow(), options.Value.Agents.OfflineAfter));
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
        DupliDbContext db, IOptions<DupliServerOptions> options, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var offlineAfter = options.Value.Agents.OfflineAfter;
        var agents = (await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct))
            .Select(a => ToDto(a, now, offlineAfter)).ToList();

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

    private static async Task<IResult> CreateReleaseAsync(CreateReleaseRequest request, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit))
            throw ApiException.BadRequest("sha256 must be 64 hex characters");
        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw ApiException.BadRequest("sourceUrl must be an absolute https URL");

        // One transaction: a conflicting insert must not leave the platform without a current release.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Releases.AnyAsync(r => r.Product == Tools.ResticMirror.Product && r.Version == request.Version && r.Platform == request.Platform, ct))
            throw ApiException.Conflict("This release already exists");

        if (request.MakeCurrent)
        {
            await db.Releases
                .Where(r => r.Product == Tools.ResticMirror.Product && r.Platform == request.Platform && r.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct);
        }

        var release = new SoftwareRelease
        {
            Id = Guid.NewGuid(),
            Product = Tools.ResticMirror.Product,
            Version = request.Version,
            Platform = request.Platform,
            SourceUrl = request.SourceUrl,
            Sha256 = request.Sha256.ToLowerInvariant(),
            IsCurrent = request.MakeCurrent,
            CreatedAt = time.GetUtcNow(),
        };
        db.Releases.Add(release);
        await SaveOrConflictAsync(db, "This release already exists", ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/tools/restic/{release.Version}/{release.Platform}", null);
    }

    private static async Task<BackupPolicy> LoadPolicyAsync(Guid id, DupliDbContext db, CancellationToken ct, bool tracking)
    {
        var query = db.Policies.Include(p => p.Sources).Where(p => p.Id == id);
        return await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Policy");
    }

    private static async Task SaveOrConflictAsync(DupliDbContext db, string conflictMessage, CancellationToken ct)
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

    private static AgentDto ToDto(Agent a, DateTimeOffset now, TimeSpan offlineAfter) => new(
        a.Id, a.Name, a.Status, a.IsOnline(now, offlineAfter), a.Hostname, a.OsVersion, a.Version, a.ResticVersion,
        a.LastHeartbeatAt, a.LastBackupAt, a.FreeDiskSpace, a.StorageTargetId, a.StoragePrefix, a.CreatedAt, a.EnrolledAt);

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

    private static JobDto ToDto(Job j) => new(
        j.Id, j.AgentId, j.PolicyId, j.Type, j.Trigger, j.State, j.CreatedAt, j.ScheduledAt, j.ExpiresAt,
        j.StartedAt, j.CompletedAt, j.CancelRequested, j.Error,
        j.ResultItems is null ? [] : JsonSerializer.Deserialize<List<JobItemResultDto>>(j.ResultItems, DupliJson.Options) ?? []);

    private static RunDto ToDto(BackupRun r) => new(
        r.Id, r.JobId, r.PolicyId, r.AgentId, r.StartedAt, r.CompletedAt, r.Status, r.BytesProcessed, r.BytesAdded,
        JsonSerializer.Deserialize<List<JobItemResultDto>>(r.Items, DupliJson.Options) ?? [], r.ErrorMessage);
}
