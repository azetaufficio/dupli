using System.Text.Json;
using Dupli.Contracts;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Api;

/// <summary>Agent-level PostgreSQL connections, referenced by postgres policy sources via <c>connectionId</c>.</summary>
public static class ConnectionsApi
{
    public static void MapConnectionsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/agents/{id:guid}/connections", ListAsync);
        admin.MapPost("/agents/{id:guid}/connections", CreateAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
        admin.MapGet("/connections/{id:guid}", GetAsync);
        admin.MapPut("/connections/{id:guid}", UpdateAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
        admin.MapDelete("/connections/{id:guid}", DeleteAsync).RequireAuthorization(AuthConstants.OperatorPolicy);
    }

    private static async Task<IEnumerable<PgConnectionDto>> ListAsync(Guid id, DupliDbContext db, CancellationToken ct)
    {
        var connections = await db.PgConnections.AsNoTracking().Where(c => c.AgentId == id).OrderBy(c => c.Name).ToListAsync(ct);
        var secretNames = (await db.AgentSecrets.AsNoTracking().Where(s => s.AgentId == id).Select(s => s.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        return connections.Select(c => ToDto(c, secretNames.Contains(c.PasswordSecret)));
    }

    private static async Task<IResult> CreateAsync(
        Guid id, PgConnectionRequest request, DupliDbContext db, SecretProtector protector, TimeProvider time, CancellationToken ct)
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
            // An internal escrow key, never user-facing. Its own id, independent from the connection's,
            // so a connection could later escrow more than one named secret (e.g. a TLS client key).
            PasswordSecret = Guid.NewGuid().ToString("N"),
            BinDirectory = string.IsNullOrWhiteSpace(request.BinDirectory) ? null : request.BinDirectory.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PgConnections.Add(connection);
        await AdminApi.SaveOrConflictAsync(db, "A connection with this name already exists for the agent", ct);

        if (!string.IsNullOrEmpty(request.Password))
            await SetPasswordAsync(db, id, connection.PasswordSecret, request.Password, protector, time, ct);
        var passwordSet = await PasswordSetAsync(db, id, connection.PasswordSecret, ct);
        return Results.Created($"/api/admin/connections/{connection.Id}", ToDto(connection, passwordSet));
    }

    private static async Task<PgConnectionDto> GetAsync(Guid id, DupliDbContext db, CancellationToken ct)
    {
        var connection = await db.PgConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        var passwordSet = await PasswordSetAsync(db, connection.AgentId, connection.PasswordSecret, ct);
        return ToDto(connection, passwordSet);
    }

    private static async Task<PgConnectionDto> UpdateAsync(
        Guid id, PgConnectionRequest request, DupliDbContext db, SecretProtector protector, TimeProvider time, CancellationToken ct)
    {
        Validate(request);
        var connection = await db.PgConnections.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        connection.Name = request.Name.Trim();
        connection.Host = request.Host.Trim();
        connection.Port = request.Port;
        connection.Username = request.Username.Trim();
        connection.BinDirectory = string.IsNullOrWhiteSpace(request.BinDirectory) ? null : request.BinDirectory.Trim();
        connection.UpdatedAt = time.GetUtcNow();
        await AdminApi.SaveOrConflictAsync(db, "A connection with this name already exists for the agent", ct);

        if (!string.IsNullOrEmpty(request.Password))
            await SetPasswordAsync(db, connection.AgentId, connection.PasswordSecret, request.Password, protector, time, ct);
        var passwordSet = await PasswordSetAsync(db, connection.AgentId, connection.PasswordSecret, ct);
        return ToDto(connection, passwordSet);
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
    }

    /// <summary>Upserts the escrowed password (composite key, no native EF Core upsert).</summary>
    private static async Task SetPasswordAsync(
        DupliDbContext db, Guid agentId, string name, string password, SecretProtector protector, TimeProvider time, CancellationToken ct)
    {
        var existing = await db.AgentSecrets.SingleOrDefaultAsync(s => s.AgentId == agentId && s.Name == name, ct);
        var now = time.GetUtcNow();
        if (existing is null)
            db.AgentSecrets.Add(new AgentSecret { AgentId = agentId, Name = name, ValueProtected = protector.Protect(password), UpdatedAt = now });
        else
        {
            existing.ValueProtected = protector.Protect(password);
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    private static Task<bool> PasswordSetAsync(DupliDbContext db, Guid agentId, string name, CancellationToken ct) =>
        db.AgentSecrets.AsNoTracking().AnyAsync(s => s.AgentId == agentId && s.Name == name, ct);

    private static PgConnectionDto ToDto(PgConnection c, bool passwordSet) =>
        new(c.Id, c.AgentId, c.Name, c.Host, c.Port, c.Username, c.PasswordSecret, c.BinDirectory, c.CreatedAt, c.UpdatedAt, passwordSet);
}
