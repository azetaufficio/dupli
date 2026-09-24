using System.Text.Json;
using System.Text.RegularExpressions;
using Dupli.Contracts;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Api;

/// <summary>Agent-level PostgreSQL connections, referenced by postgres policy sources via <c>connectionId</c>.</summary>
public static partial class ConnectionsApi
{
    // Matches the agent secret store's file-name-safe charset (dupli-agent secret set <name>).
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SecretNamePattern();

    public static void MapConnectionsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/agents/{id:guid}/connections", ListAsync);
        admin.MapPost("/agents/{id:guid}/connections", CreateAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
        admin.MapGet("/connections/{id:guid}", GetAsync);
        admin.MapPut("/connections/{id:guid}", UpdateAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
        admin.MapDelete("/connections/{id:guid}", DeleteAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
    }

    private static async Task<IEnumerable<PgConnectionDto>> ListAsync(Guid id, DupliDbContext db, CancellationToken ct) =>
        (await db.PgConnections.AsNoTracking().Where(c => c.AgentId == id).OrderBy(c => c.Name).ToListAsync(ct)).Select(ToDto);

    private static async Task<IResult> CreateAsync(Guid id, PgConnectionRequest request, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        Validate(request);
        if (!await db.Agents.AnyAsync(a => a.Id == id, ct))
            throw ApiException.NotFound("Agent");

        var now = time.GetUtcNow();
        var connection = new PgConnection
        {
            Id = Guid.NewGuid(),
            AgentId = id,
            Name = request.Name.Trim(),
            Host = request.Host.Trim(),
            Port = request.Port,
            Username = request.Username.Trim(),
            PasswordSecret = request.PasswordSecret.Trim(),
            BinDirectory = string.IsNullOrWhiteSpace(request.BinDirectory) ? null : request.BinDirectory.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PgConnections.Add(connection);
        await AdminApi.SaveOrConflictAsync(db, "A connection with this name already exists for the agent", ct);
        return Results.Created($"/api/admin/connections/{connection.Id}", ToDto(connection));
    }

    private static async Task<PgConnectionDto> GetAsync(Guid id, DupliDbContext db, CancellationToken ct) =>
        ToDto(await db.PgConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connection"));

    private static async Task<PgConnectionDto> UpdateAsync(Guid id, PgConnectionRequest request, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        Validate(request);
        var connection = await db.PgConnections.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        connection.Name = request.Name.Trim();
        connection.Host = request.Host.Trim();
        connection.Port = request.Port;
        connection.Username = request.Username.Trim();
        connection.PasswordSecret = request.PasswordSecret.Trim();
        connection.BinDirectory = string.IsNullOrWhiteSpace(request.BinDirectory) ? null : request.BinDirectory.Trim();
        connection.UpdatedAt = time.GetUtcNow();
        await AdminApi.SaveOrConflictAsync(db, "A connection with this name already exists for the agent", ct);
        return ToDto(connection);
    }

    private static async Task<IResult> DeleteAsync(Guid id, DupliDbContext db, CancellationToken ct)
    {
        var connection = await db.PgConnections.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        if (await IsInUseAsync(connection, db, ct))
            throw ApiException.Conflict("This connection is still referenced by a policy source");

        db.PgConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<bool> IsInUseAsync(PgConnection connection, DupliDbContext db, CancellationToken ct)
    {
        var specs = await db.Sources.AsNoTracking()
            .Where(s => s.Type == BackupSourceType.PostgreSql && db.Policies.Any(p => p.Id == s.PolicyId && p.AgentId == connection.AgentId))
            .Select(s => s.Spec)
            .ToListAsync(ct);
        return specs.Any(spec =>
            (JsonSerializer.Deserialize<PolicySourceDto>(spec, DupliJson.Options) as PolicyPostgresSourceDto)?.ConnectionId == connection.Id);
    }

    private static void Validate(PgConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw ApiException.BadRequest("Connection name is required");
        if (string.IsNullOrWhiteSpace(request.Host))
            throw ApiException.BadRequest("Host is required");
        if (request.Port is <= 0 or > 65535)
            throw ApiException.BadRequest("Invalid port");
        if (string.IsNullOrWhiteSpace(request.Username))
            throw ApiException.BadRequest("Username is required");
        if (string.IsNullOrWhiteSpace(request.PasswordSecret) || !SecretNamePattern().IsMatch(request.PasswordSecret))
            throw ApiException.BadRequest("Password secret name must be letters, digits, '_' or '-'");
    }

    private static PgConnectionDto ToDto(PgConnection c) =>
        new(c.Id, c.AgentId, c.Name, c.Host, c.Port, c.Username, c.PasswordSecret, c.BinDirectory, c.CreatedAt, c.UpdatedAt);
}
