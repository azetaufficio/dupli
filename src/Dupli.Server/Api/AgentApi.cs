using System.Text.Json;
using Dupli.Contracts.Agents;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Logs;
using Dupli.Server.Agents;
using Dupli.Server.Auth;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Hosting;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Jobs;
using Dupli.Server.Tools;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Api;

/// <summary>Agent-facing API. Every call is initiated by the agent (polling), never by the server.</summary>
public static class AgentApi
{
    private const int MaxLogEntries = 1000;
    private const int MaxLogMessageLength = 8192;

    public static void MapAgentApi(this IEndpointRouteBuilder app)
    {
        var anonymous = app.MapGroup("/api").AllowAnonymous();
        anonymous.MapPost("/agents/register", RegisterAsync).RequireRateLimiting(RateLimiting.AnonymousPolicy);
        anonymous.MapPost("/agents/token", (AgentTokenRequest request, EnrollmentService enrollment, CancellationToken ct) =>
            enrollment.IssueTokenAsync(request, ct)).RequireRateLimiting(RateLimiting.AnonymousPolicy);
        anonymous.MapGet("/tools/restic/manifest", ManifestAsync);
        anonymous.MapGet("/tools/restic/{version}/{platform}", (string version, string platform, ReleaseMirror mirror, CancellationToken ct) =>
            DownloadAsync(ReleaseMirror.ResticProduct, version, platform, mirror, ct));
        anonymous.MapGet("/tools/agent/{version}/{platform}", (string version, string platform, ReleaseMirror mirror, CancellationToken ct) =>
            DownloadAsync(ReleaseMirror.AgentProduct, version, platform, mirror, ct));

        var agent = app.MapGroup("/api").RequireAuthorization(AuthConstants.AgentPolicy);
        agent.MapPost("/agents/heartbeat", HeartbeatAsync);
        agent.MapPost("/agents/secret/rotate", (HttpContext http, EnrollmentService enrollment, CancellationToken ct) =>
            enrollment.RotateSecretAsync(http.User.AgentId(), ct));
        agent.MapGet("/agents/{agentId:guid}/jobs", JobsAsync);
        agent.MapPost("/agents/logs", (AgentLogBatchDto batch, HttpContext http, DupliDbContext db, CancellationToken ct) =>
            StoreLogsAsync(http.User.AgentId(), null, batch, db, ct));

        agent.MapPost("/jobs/{jobId:guid}/started", (Guid jobId, JobStartedRequest _, HttpContext http, JobService jobs, CancellationToken ct) =>
            jobs.StartedAsync(http.User.AgentId(), jobId, ct));
        agent.MapPost("/jobs/{jobId:guid}/progress", (Guid jobId, JobProgressRequest _, HttpContext http, JobService jobs, CancellationToken ct) =>
            jobs.ProgressAsync(http.User.AgentId(), jobId, ct));
        agent.MapPost("/jobs/{jobId:guid}/completed", ReportAsync);
        agent.MapPost("/jobs/{jobId:guid}/failed", ReportAsync);
        agent.MapPost("/jobs/{jobId:guid}/logs", (Guid jobId, AgentLogBatchDto batch, HttpContext http, DupliDbContext db, CancellationToken ct) =>
            StoreLogsAsync(http.User.AgentId(), jobId, batch, db, ct));
        agent.MapPost("/agents/jobs/{jobId:guid}/credentials", JobCredentialsAsync);
    }

    public static string PublicBaseUrl(HttpRequest request, DupliServerOptions options) =>
        options.PublicUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static Task<RegisterAgentResponse> RegisterAsync(
        RegisterAgentRequest request, HttpRequest http, EnrollmentService enrollment,
        IOptions<DupliServerOptions> options, CancellationToken ct) =>
        enrollment.RegisterAsync(request, PublicBaseUrl(http, options.Value), ct);

    private static async Task<IResult> ManifestAsync(
        HttpRequest http, ReleaseMirror mirror, IOptions<DupliServerOptions> options, CancellationToken ct, string? platform = null)
    {
        var manifest = await mirror.GetManifestAsync(ReleaseMirror.ResticProduct, platform ?? ReleaseMirror.DefaultPlatform, PublicBaseUrl(http, options.Value), ct);
        return manifest is null ? Results.NotFound() : Results.Ok(manifest);
    }

    private static async Task<IResult> DownloadAsync(string product, string version, string platform, ReleaseMirror mirror, CancellationToken ct)
    {
        var asset = await mirror.GetAssetAsync(product, version, platform, ct);
        return asset is { } a ? Results.File(a.Path, "application/octet-stream", a.FileName) : Results.NotFound();
    }

    private static async Task<HeartbeatResponse> HeartbeatAsync(
        HeartbeatRequest request, HttpContext http, DupliDbContext db,
        IOptions<DupliServerOptions> options, TimeProvider time, DesiredVersionResolver resolver, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([http.User.AgentId()], ct) ?? throw ApiException.NotFound("Agent");
        var now = time.GetUtcNow();
        agent.LastHeartbeatAt = now;
        agent.Hostname = request.Hostname;
        agent.Version = request.Version;
        agent.ResticVersion = request.ResticVersion ?? agent.ResticVersion;
        agent.OsVersion = request.OsVersion;
        agent.FreeDiskSpace = request.FreeDiskSpace;
        if (!string.IsNullOrWhiteSpace(request.Platform))
            agent.Platform = request.Platform;
        agent.LauncherManaged = request.LauncherManaged;
        if (request.LastUpdate is { } lastUpdate)
        {
            agent.LastUpdateVersion = lastUpdate.Version;
            agent.LastUpdateOutcome = lastUpdate.Outcome.ToString();
            agent.LastUpdateError = lastUpdate.Error;
            agent.LastUpdateAt = lastUpdate.At;
        }
        agent.ResticUpdateError = request.ResticUpdateError;
        // Null means an agent too old to report it: leave whatever version we last recorded as applied.
        if (request.StorageCredentialsVersion is { } appliedCredentials)
            agent.S3CredentialsAppliedVersion = appliedCredentials;

        var (desiredAgent, desiredRestic) = await resolver.ResolveAsync(agent, ct);
        agent.OutdatedSince = desiredAgent is not null && agent.Version != desiredAgent.Version
            ? agent.OutdatedSince ?? now
            : null;
        await db.SaveChangesAsync(ct);

        var publicBaseUrl = PublicBaseUrl(http.Request, options.Value);
        return new HeartbeatResponse
        {
            ServerTime = now,
            PollIntervalSeconds = options.Value.Agents.PollIntervalSeconds,
            DesiredAgent = desiredAgent is null ? null : ReleaseMirror.ToManifest(desiredAgent, publicBaseUrl),
            DesiredRestic = desiredRestic is null ? null : ReleaseMirror.ToManifest(desiredRestic, publicBaseUrl),
            StorageCredentialsVersion = agent.S3CredentialsVersion,
        };
    }

    /// <summary>
    /// Just-in-time credentials for one Running job: repository + whatever PostgreSQL passwords its sources
    /// reference. Never cached: audit-logged by <see cref="JobCredentialsService"/> (names only, never values).
    /// </summary>
    private static async Task<IResult> JobCredentialsAsync(Guid jobId, HttpContext http, JobCredentialsService credentials, CancellationToken ct)
    {
        var response = await credentials.GetAsync(http.User.AgentId(), jobId, ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(response);
    }

    private static Task<IReadOnlyList<AgentJobDto>> JobsAsync(Guid agentId, HttpContext http, JobService jobs, CancellationToken ct)
    {
        if (agentId != http.User.AgentId())
            throw new ApiException(StatusCodes.Status403Forbidden, "Agents can only poll their own jobs");
        return jobs.AssignAsync(agentId, ct);
    }

    private static async Task<IResult> ReportAsync(Guid jobId, JobResultDto result, HttpContext http, JobService jobs, CancellationToken ct)
    {
        await jobs.ReportResultAsync(http.User.AgentId(), jobId, result, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> StoreLogsAsync(Guid agentId, Guid? jobId, AgentLogBatchDto batch, DupliDbContext db, CancellationToken ct)
    {
        if (batch.Entries.Count > MaxLogEntries)
            throw ApiException.BadRequest($"At most {MaxLogEntries} entries per batch");

        foreach (var e in batch.Entries)
        {
            db.Logs.Add(new AgentLog
            {
                AgentId = agentId,
                JobId = jobId ?? (Guid.TryParse(e.JobId, out var j) ? j : null),
                Timestamp = e.Timestamp.ToUniversalTime(),
                Level = e.Level,
                Message = Truncate(e.Message),
                Exception = e.Exception is null ? null : Truncate(e.Exception),
                Properties = e.Properties is null ? null : JsonSerializer.Serialize(e.Properties),
            });
        }

        if (batch.Dropped > 0)
        {
            db.Logs.Add(new AgentLog
            {
                AgentId = agentId,
                JobId = jobId,
                Timestamp = batch.Entries.Count > 0 ? batch.Entries[^1].Timestamp.ToUniversalTime() : DateTimeOffset.UtcNow,
                Level = "Warning",
                Message = $"{batch.Dropped} log events dropped by the agent (upload buffer full)",
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string Truncate(string value) =>
        value.Length <= MaxLogMessageLength ? value : value[..MaxLogMessageLength] + "…";
}
