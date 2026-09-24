using System.Security.Claims;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Auth;

/// <summary>Claims of an operator session cookie. The role is not stored: it is read from the database per request.</summary>
public static class OperatorClaims
{
    public const string UserId = "sub";
    public const string Role = "dupli:role";
    public const string Name = "name";
    public const string Email = "email";
    public const string TenantId = "tid";
    public const string ObjectId = "oid";
    public const string PreferredUsername = "preferred_username";

    public static Guid? UserIdOf(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(UserId), out var id) ? id : null;

    public static OperatorRole? RoleOf(ClaimsPrincipal user) =>
        Enum.TryParse<OperatorRole>(user.FindFirstValue(Role), out var role) ? role : null;
}

/// <summary>What the identity provider says about the person signing in.</summary>
public sealed record OperatorIdentity(string TenantId, string ObjectId, string? Email, string? PreferredUsername, string? DisplayName)
{
    /// <summary>Addresses an invitation may match: <c>email</c> and <c>preferred_username</c> (UPN), normalized.</summary>
    public IReadOnlyList<string> Addresses { get; } =
        new[] { Email, PreferredUsername }
            .Where(a => !string.IsNullOrWhiteSpace(a) && a.Contains('@'))
            .Select(a => OperatorDirectory.Normalize(a!))
            .Distinct()
            .ToList();

    public string Shown => Email ?? PreferredUsername ?? ObjectId;

    public static OperatorIdentity? FromClaims(ClaimsPrincipal principal)
    {
        var tid = principal.FindFirstValue(OperatorClaims.TenantId);
        var oid = principal.FindFirstValue(OperatorClaims.ObjectId);
        if (string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid))
            return null;
        return new OperatorIdentity(tid, oid,
            principal.FindFirstValue(OperatorClaims.Email),
            principal.FindFirstValue(OperatorClaims.PreferredUsername),
            principal.FindFirstValue(OperatorClaims.Name));
    }
}

/// <summary>
/// Signs operators in against the <c>operator_user</c> table: match by Entra ID <c>tid</c>+<c>oid</c>, else bind a
/// pending invitation by email, else (empty table only) create the bootstrap owner. Also caches the per-request
/// session lookups, invalidated by every change made through this server.
/// </summary>
public sealed class OperatorDirectory(
    DupliDbContext db,
    IMemoryCache cache,
    TimeProvider time,
    IOptions<DupliServerOptions> options,
    ILogger<OperatorDirectory> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    // Serializes sign-in bindings, the bootstrap owner creation and the last-owner checks of user management.
    private const long LockKey = 0x6475706c69_7573; // "dupli" "us"

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Takes the operator table lock for the current transaction.</summary>
    public static Task LockAsync(DupliDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);

    /// <summary>The session cookie principal, or null when this person may not use Dupli.</summary>
    public async Task<ClaimsPrincipal?> SignInAsync(OperatorIdentity identity, CancellationToken ct)
    {
        var tenant = options.Value.Auth.EntraId.TenantId;
        if (!string.Equals(identity.TenantId, tenant, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Sign-in refused for {Email} ({ObjectId}): tenant {TenantId} is not the configured one",
                identity.Shown, identity.ObjectId, identity.TenantId);
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);

        var user = await FindOrBindAsync(identity, ct);
        if (user is null)
            return null;

        user.LastLoginAt = time.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(identity.DisplayName))
            user.DisplayName = identity.DisplayName;
        if (identity.Email is { } email && Normalize(email) != Normalize(user.Email)
            && !await db.OperatorUsers.AnyAsync(u => u.Id != user.Id && u.Email.ToLower() == Normalize(email), ct))
            user.Email = email.Trim();

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        Invalidate(user.Id);

        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(OperatorClaims.UserId, user.Id.ToString()),
                new Claim(OperatorClaims.Name, user.DisplayName ?? user.Email),
                new Claim(OperatorClaims.Email, user.Email),
                new Claim(OperatorClaims.TenantId, identity.TenantId),
                new Claim(OperatorClaims.ObjectId, identity.ObjectId),
            ],
            "Dupli", OperatorClaims.Name, OperatorClaims.Role));
    }

    private async Task<OperatorUser?> FindOrBindAsync(OperatorIdentity identity, CancellationToken ct)
    {
        var bound = await db.OperatorUsers.SingleOrDefaultAsync(u => u.TenantId == identity.TenantId && u.ObjectId == identity.ObjectId, ct);
        if (bound is not null)
            return bound.IsActive ? bound : Refuse(identity, "the user is disabled");

        var now = time.GetUtcNow();
        if (!await db.OperatorUsers.AnyAsync(ct))
        {
            var bootstrap = options.Value.Auth.BootstrapOwnerEmail;
            if (string.IsNullOrWhiteSpace(bootstrap) || !identity.Addresses.Contains(Normalize(bootstrap)))
                return Refuse(identity, "no operator exists yet and this is not the bootstrap owner");

            var owner = new OperatorUser
            {
                Id = Guid.NewGuid(),
                Email = (identity.Email ?? bootstrap).Trim(),
                Role = OperatorRole.Owner,
                TenantId = identity.TenantId,
                ObjectId = identity.ObjectId,
                CreatedAt = now,
                CreatedBy = "bootstrap",
                UpdatedAt = now,
                UpdatedBy = "bootstrap",
            };
            db.OperatorUsers.Add(owner);
            logger.LogWarning("Bootstrap owner {Email} ({ObjectId}) created on first sign-in", owner.Email, identity.ObjectId);
            return owner;
        }

        var addresses = identity.Addresses.ToList();
        var invitation = await db.OperatorUsers
            .Where(u => u.ObjectId == null && addresses.Contains(u.Email.ToLower()))
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (invitation is null)
            return Refuse(identity, "no invitation matches");
        if (!invitation.IsActive)
            return Refuse(identity, "the invitation is disabled");

        invitation.TenantId = identity.TenantId;
        invitation.ObjectId = identity.ObjectId;
        invitation.UpdatedAt = now;
        invitation.UpdatedBy = invitation.Email;
        logger.LogInformation("Invitation {Email} bound to {ObjectId} on first sign-in", invitation.Email, identity.ObjectId);
        return invitation;
    }

    private OperatorUser? Refuse(OperatorIdentity identity, string reason)
    {
        logger.LogWarning("Sign-in refused for {Email} ({ObjectId}): {Reason}", identity.Shown, identity.ObjectId, reason);
        return null;
    }

    /// <summary>Current state of a signed-in operator, cached for a few seconds; null when removed.</summary>
    public async Task<OperatorUser?> GetAsync(Guid id, CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey(id), out OperatorUser? cached))
            return cached;
        var user = await db.OperatorUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        cache.Set(CacheKey(id), user, CacheLifetime);
        return user;
    }

    public void Invalidate(Guid id) => cache.Remove(CacheKey(id));

    private static string CacheKey(Guid id) => $"operator-user:{id}";

    /// <summary>Checked once the schema is up to date: an EntraId server with no operator must know who the first one is.</summary>
    public static async Task EnsureBootstrapConfiguredAsync(IServiceProvider services, DupliServerOptions options, ILogger logger)
    {
        if (options.Auth.Mode != AuthMode.EntraId)
            return;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DupliDbContext>();
        if (await db.OperatorUsers.AnyAsync())
            return;
        if (string.IsNullOrWhiteSpace(options.Auth.BootstrapOwnerEmail))
            throw new InvalidOperationException(
                "No operator user exists: set Dupli:Auth:BootstrapOwnerEmail to the email of the first owner");
        logger.LogWarning("No operator user exists: {Email} becomes owner on first sign-in", options.Auth.BootstrapOwnerEmail);
    }
}
