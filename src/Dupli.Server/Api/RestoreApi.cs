using System.Text.RegularExpressions;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Postgres;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Jobs;
using Dupli.Server.Restore;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Api;

/// <summary>
/// Snapshot list and browse (read by the server from the repository) and restore requests (executed by the agent).
/// Mapped under the admin group.
/// </summary>
public static partial class RestoreApi
{
    public static void MapRestoreApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/agents/{id:guid}/snapshots", ListSnapshotsAsync);
        // Snapshot contents (file names) and restores need the Operator role; the snapshot list is readable by Viewers.
        admin.MapGet("/agents/{id:guid}/snapshots/{snapshotId}/tree", ListTreeAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
        admin.MapPost("/agents/{id:guid}/restores", CreateRestoreAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
    }

    private static async Task<IEnumerable<SnapshotDto>> ListSnapshotsAsync(
        Guid id, RepositoryBrowser browser, DupliDbContext db, CancellationToken ct, bool refresh = false)
    {
        var snapshots = await browser.ListSnapshotsAsync(id, refresh, ct);
        var policies = await db.Policies.AsNoTracking().Include(p => p.Sources).Where(p => p.AgentId == id).ToListAsync(ct);
        var policyNames = policies.ToDictionary(p => p.Id, p => p.Name);
        var connectionsBySource = policies
            .SelectMany(p => p.Sources
                .Where(s => s.Type == BackupSourceType.PostgreSql)
                .Select(s => (Key: (PolicyId: p.Id, SourceId: s.SourceKey), Source: PolicySpecBuilder.ToDto(s) as PolicyPostgresSourceDto)))
            .Where(x => x.Source is not null)
            .ToDictionary(x => x.Key, x => x.Source!.ConnectionId);
        return snapshots.Select(s => ToDto(s, policyNames, connectionsBySource));
    }

    private static async Task<IEnumerable<SnapshotNodeDto>> ListTreeAsync(
        Guid id, string snapshotId, RepositoryBrowser browser, CancellationToken ct, string path = "/")
    {
        ValidateSnapshotId(snapshotId);
        ValidatePath(path);
        var nodes = await browser.ListDirectoryAsync(id, snapshotId, path, ct);
        return nodes.Select(n => new SnapshotNodeDto(n.Name, n.Path, n.Type, n.Size, n.ModifiedAt));
    }

    private static async Task<IResult> CreateRestoreAsync(
        Guid id, CreateRestoreRequest request, RepositoryBrowser browser, DupliDbContext db, JobService jobs,
        TimeProvider time, CancellationToken ct)
    {
        ValidateSnapshotId(request.SnapshotId);
        foreach (var include in request.Includes)
            ValidatePath(include);
        // The VM is usually Windows while the server is Linux: check the shape, the agent resolves and guards it.
        if (!string.IsNullOrWhiteSpace(request.TargetDirectory) && !AbsolutePathRegex().IsMatch(request.TargetDirectory.Trim()))
            throw ApiException.BadRequest("Target directory must be an absolute path on the VM (e.g. D:\\Restore\\x)");
        if (!string.IsNullOrWhiteSpace(request.NewDatabase) && request.ConnectionId is null)
            throw ApiException.BadRequest("A target connection is required to load into a new database");

        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Agent");
        if (agent.Status != AgentStatus.Active)
            throw ApiException.Conflict("The agent is not active: a restore runs on the VM");

        PgConnection? connection = null;
        if (request.ConnectionId is { } connectionId)
        {
            connection = await db.PgConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct)
                ?? throw ApiException.BadRequest("Unknown connection");
            if (connection.AgentId != id)
                throw ApiException.BadRequest("The connection does not belong to this agent");
        }

        var snapshot = await browser.FindSnapshotAsync(id, request.SnapshotId, ct)
            ?? throw ApiException.NotFound($"Snapshot {request.SnapshotId}");

        var connections = await PolicySpecBuilder.LoadConnectionsAsync(db, id, ct);
        var policies = (await db.Policies.AsNoTracking().Include(p => p.Sources).Where(p => p.AgentId == id).ToListAsync(ct))
            .Select(p => PolicySpecBuilder.Build(p, connections))
            .ToList();

        var payload = new RestoreJobPayload
        {
            SnapshotId = snapshot.Id,
            Includes = request.Includes,
            TargetDirectory = string.IsNullOrWhiteSpace(request.TargetDirectory) ? null : request.TargetDirectory.Trim(),
            Postgres = string.IsNullOrWhiteSpace(request.NewDatabase) ? null : PostgresRestore(snapshot, request.NewDatabase.Trim(), connection!),
            Policies = policies,
        };

        var job = await jobs.CreateRestoreJobAsync(id, payload, time.GetUtcNow(), ct)
            ?? throw ApiException.Conflict("A restore is already pending for this agent");
        return Results.Accepted($"/api/admin/jobs/{job.Id}", await AdminApi.ToDtoAsync(job, db, ct));
    }

    private static PostgresRestoreDto PostgresRestore(SnapshotInfo snapshot, string newDatabase, PgConnection connection)
    {
        var tags = ParseTags(snapshot.Tags);
        if (tags.GetValueOrDefault("type") != "pg" || tags.GetValueOrDefault("db") is not { } database)
            throw ApiException.BadRequest("Restoring into a database needs a PostgreSQL snapshot");
        if (database == PostgresDumpProvider.GlobalsName)
            throw ApiException.BadRequest("Roles and tablespaces (globals) are never applied automatically: restore the file");
        if (!PostgresRestorer.IsValidDatabaseName(newDatabase))
            throw ApiException.BadRequest("Database name: letters, digits, '_' or '-', not starting with a digit, max 63 characters");
        if (string.Equals(newDatabase, database, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("The new database must have a different name from the backed-up one");

        var sourceId = tags.GetValueOrDefault("source") ?? throw ApiException.BadRequest("Snapshot is missing its source tag");
        var source = new PostgresSourceDto
        {
            SourceId = sourceId,
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            PasswordSecret = connection.PasswordSecret,
            BinDirectory = connection.BinDirectory,
        };

        return new PostgresRestoreDto { Source = source, Database = database, NewDatabase = newDatabase };
    }

    private static SnapshotDto ToDto(
        SnapshotInfo s, IReadOnlyDictionary<Guid, string> policyNames, IReadOnlyDictionary<(Guid PolicyId, string SourceId), Guid> connectionsBySource)
    {
        var tags = ParseTags(s.Tags);
        Guid? policyId = Guid.TryParse(tags.GetValueOrDefault("policy"), out var pid) ? pid : null;
        var sourceId = tags.GetValueOrDefault("source");
        Guid? connectionId = policyId is { } p && sourceId is not null && connectionsBySource.TryGetValue((p, sourceId), out var cid)
            ? cid
            : null;
        return new SnapshotDto(
            s.Id, s.ShortId, s.Time, s.Host, s.Paths, s.Tags,
            policyId,
            policyId is { } pp ? policyNames.GetValueOrDefault(pp) : null,
            sourceId,
            tags.GetValueOrDefault("type"),
            tags.GetValueOrDefault("db"),
            connectionId);
    }

    /// <summary><c>key=value</c> tags written by the agent (<c>BackupTags</c>); first value wins.</summary>
    private static Dictionary<string, string> ParseTags(IEnumerable<string> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            if (tag.IndexOf('=') is > 0 and var i)
                result.TryAdd(tag[..i], tag[(i + 1)..]);
        return result;
    }

    private static void ValidateSnapshotId(string snapshotId)
    {
        if (!SnapshotIdRegex().IsMatch(snapshotId))
            throw ApiException.BadRequest("Invalid snapshot id");
    }

    /// <summary>restic snapshot paths: absolute, forward slashes, no traversal, not an option.</summary>
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Split('/').Any(seg => seg is ".." or ".") || path.Contains('\0'))
            throw ApiException.BadRequest($"Invalid snapshot path '{path}'");
    }

    // C:\x, C:/x or /x (Linux agents).
    [GeneratedRegex(@"^([A-Za-z]:[\\/]|/)")]
    private static partial Regex AbsolutePathRegex();

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex SnapshotIdRegex();
}
