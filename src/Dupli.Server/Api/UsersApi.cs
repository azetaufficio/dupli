using System.Net.Mail;
using System.Security.Claims;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Api;

/// <summary>
/// Operator users under <c>/api/admin/users</c>: Owners from the web UI, or a break-glass session / the admin key.
/// The last bound active Owner cannot be demoted, disabled or deleted, except by break-glass or the admin key.
/// </summary>
public static class UsersApi
{
    public static void MapUsersApi(this IEndpointRouteBuilder app)
    {
        // Not under the /api/admin group: its Viewer policy would also be required, and a break-glass session has no role.
        var users = app.MapGroup("/api/admin/users").RequireAuthorization(AuthConstants.UsersPolicy).RequireAntiforgeryForCookies();
        users.MapGet("", ListAsync);
        users.MapPost("", InviteAsync);
        users.MapPut("/{id:guid}", UpdateAsync);
        users.MapPost("/{id:guid}/disable", (Guid id, ClaimsPrincipal actor, DupliDbContext db, OperatorDirectory directory, TimeProvider time, CancellationToken ct) =>
            SetDisabledAsync(id, disabled: true, actor, db, directory, time, ct));
        users.MapPost("/{id:guid}/enable", (Guid id, ClaimsPrincipal actor, DupliDbContext db, OperatorDirectory directory, TimeProvider time, CancellationToken ct) =>
            SetDisabledAsync(id, disabled: false, actor, db, directory, time, ct));
        users.MapDelete("/{id:guid}", DeleteAsync);

        users.MapGet("/{id:guid}/notification-preferences", GetPreferencesAsync);
        users.MapPut("/{id:guid}/notification-preferences", SetPreferencesAsync);
    }

    private static async Task<IEnumerable<NotificationPreferenceDto>> GetPreferencesAsync(Guid id, DupliDbContext db, CancellationToken ct) =>
        await NotificationsApi.GetPreferencesAsync(await LoadUserAsync(id, db, ct), db, ct);

    private static async Task<IEnumerable<NotificationPreferenceDto>> SetPreferencesAsync(
        Guid id, List<NotificationPreferenceDto> request, DupliDbContext db, CancellationToken ct)
    {
        var user = await LoadUserAsync(id, db, ct);
        await NotificationsApi.SetPreferencesAsync(id, request, db, ct);
        return await NotificationsApi.GetPreferencesAsync(user, db, ct);
    }

    private static async Task<OperatorUser> LoadUserAsync(Guid id, DupliDbContext db, CancellationToken ct) =>
        await db.OperatorUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User");

    private static async Task<IEnumerable<OperatorUserDto>> ListAsync(DupliDbContext db, CancellationToken ct) =>
        (await db.OperatorUsers.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct)).Select(ToDto);

    private static async Task<IResult> InviteAsync(InviteOperatorRequest request, ClaimsPrincipal actor, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        var email = ValidEmail(request.Email);
        ValidRole(request.Role);
        if (await db.OperatorUsers.AnyAsync(u => u.Email.ToLower() == OperatorDirectory.Normalize(email), ct))
            throw ApiException.Conflict("A user with this email already exists");

        var now = time.GetUtcNow();
        var by = OperatorAuth.Actor(actor);
        var user = new OperatorUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            Role = request.Role,
            CreatedAt = now,
            CreatedBy = by,
            UpdatedAt = now,
            UpdatedBy = by,
        };
        db.OperatorUsers.Add(user);
        await AdminApi.SaveOrConflictAsync(db, "A user with this email already exists", ct);
        return Results.Created($"/api/admin/users/{user.Id}", ToDto(user));
    }

    private static Task<OperatorUserDto> UpdateAsync(
        Guid id, UpdateOperatorRequest request, ClaimsPrincipal actor, DupliDbContext db, OperatorDirectory directory, TimeProvider time, CancellationToken ct)
    {
        ValidRole(request.Role);
        return ChangeAsync(id, actor, db, directory, time, user => user.Role = request.Role, ct);
    }

    private static Task<OperatorUserDto> SetDisabledAsync(
        Guid id, bool disabled, ClaimsPrincipal actor, DupliDbContext db, OperatorDirectory directory, TimeProvider time, CancellationToken ct) =>
        ChangeAsync(id, actor, db, directory, time, user => user.DisabledAt = disabled ? user.DisabledAt ?? time.GetUtcNow() : null, ct);

    /// <summary>Applies <paramref name="change"/> under the operator lock, refusing to leave no usable Owner.</summary>
    private static async Task<OperatorUserDto> ChangeAsync(
        Guid id, ClaimsPrincipal actor, DupliDbContext db, OperatorDirectory directory, TimeProvider time, Action<OperatorUser> change, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OperatorDirectory.LockAsync(db, ct);
        var user = await db.OperatorUsers.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User");

        var wasUsableOwner = IsUsableOwner(user);
        change(user);
        if (wasUsableOwner && !IsUsableOwner(user))
            await EnsureAnotherOwnerAsync(user, actor, db, ct);

        user.UpdatedAt = time.GetUtcNow();
        user.UpdatedBy = OperatorAuth.Actor(actor);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        directory.Invalidate(user.Id);
        return ToDto(user);
    }

    /// <summary>Only a never-used invitation can be deleted from the UI; a real user is disabled instead.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal actor, DupliDbContext db, OperatorDirectory directory, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OperatorDirectory.LockAsync(db, ct);
        var user = await db.OperatorUsers.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User");

        if (user.IsBound && !IsPrivileged(actor))
            throw ApiException.Conflict("This user has already signed in: disable it instead");
        if (IsUsableOwner(user))
            await EnsureAnotherOwnerAsync(user, actor, db, ct);

        db.OperatorUsers.Remove(user);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        directory.Invalidate(user.Id);
        return Results.NoContent();
    }

    private static bool IsUsableOwner(OperatorUser user) => user is { Role: OperatorRole.Owner, IsBound: true, IsActive: true };

    private static async Task EnsureAnotherOwnerAsync(OperatorUser user, ClaimsPrincipal actor, DupliDbContext db, CancellationToken ct)
    {
        if (IsPrivileged(actor))
            return;
        var others = await db.OperatorUsers.AnyAsync(u =>
            u.Id != user.Id && u.Role == OperatorRole.Owner && u.ObjectId != null && u.DisabledAt == null, ct);
        if (!others)
            throw ApiException.Conflict("This is the last owner who can sign in: promote another owner first");
    }

    /// <summary>Recovery access: may bypass the last-owner rule and delete users who already signed in.</summary>
    private static bool IsPrivileged(ClaimsPrincipal actor) => OperatorAuth.IsApiKey(actor) || OperatorAuth.IsBreakGlass(actor);

    private static string ValidEmail(string? email)
    {
        var trimmed = email?.Trim() ?? "";
        if (trimmed.Length is 0 or > 320 || !MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed)
            throw ApiException.BadRequest("A valid email address is required");
        return trimmed;
    }

    private static void ValidRole(OperatorRole role)
    {
        if (!Enum.IsDefined(role))
            throw ApiException.BadRequest("role must be Owner, Operator or Viewer");
    }

    private static OperatorUserDto ToDto(OperatorUser u) =>
        new(u.Id, u.Email, u.Role, u.DisplayName, u.Language, u.IsBound, u.LastLoginAt, u.DisabledAt, u.CreatedAt, u.CreatedBy, u.UpdatedAt, u.UpdatedBy);
}
