using System.Security.Claims;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Api;

/// <summary>
/// The signed-in operator's own notification feed and preferences, under <c>/api/me</c>. Not reachable with
/// the admin key or a break-glass session: neither identifies an operator user to own a feed.
/// </summary>
public static class NotificationsApi
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static void MapNotificationsApi(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").RequireAuthorization(AuthConstants.MePolicy).RequireAntiforgeryForCookies();
        me.MapGet("/notifications", ListAsync);
        me.MapGet("/notifications/unread-count", UnreadCountAsync);
        me.MapPost("/notifications/{id:guid}/read", MarkReadAsync);
        me.MapPost("/notifications/read-all", MarkAllReadAsync);
        me.MapGet("/notification-preferences", GetMyPreferencesAsync);
        me.MapPut("/notification-preferences", SetMyPreferencesAsync);
    }

    private static Guid CallerId(ClaimsPrincipal user) =>
        OperatorAuth.IsApiKey(user) || OperatorAuth.IsBreakGlass(user)
            ? throw ApiException.Forbidden("Not available for the admin key or a break-glass session")
            : OperatorClaims.UserIdOf(user) ?? throw ApiException.Forbidden("No operator session");

    private static async Task<IEnumerable<NotificationDto>> ListAsync(
        ClaimsPrincipal user, DupliDbContext db, CancellationToken ct, bool unreadOnly = false, DateTimeOffset? before = null, int limit = DefaultLimit)
    {
        var userId = CallerId(user);
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId && n.InApp);
        if (unreadOnly)
            query = query.Where(n => n.ReadAt == null);
        if (before is { } b)
            query = query.Where(n => n.CreatedAt < b);
        return (await query.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit, 1, MaxLimit)).ToListAsync(ct)).Select(ToDto);
    }

    private static async Task<UnreadCountDto> UnreadCountAsync(ClaimsPrincipal user, DupliDbContext db, CancellationToken ct)
    {
        var userId = CallerId(user);
        var count = await db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && n.InApp && n.ReadAt == null, ct);
        return new UnreadCountDto(count);
    }

    private static async Task<IResult> MarkReadAsync(Guid id, ClaimsPrincipal user, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = CallerId(user);
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct)
            ?? throw ApiException.NotFound("Notification");
        notification.ReadAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkAllReadAsync(ClaimsPrincipal user, DupliDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = CallerId(user);
        var now = time.GetUtcNow();
        var unread = await db.Notifications.Where(n => n.UserId == userId && n.InApp && n.ReadAt == null).ToListAsync(ct);
        foreach (var notification in unread)
            notification.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IEnumerable<NotificationPreferenceDto>> GetMyPreferencesAsync(ClaimsPrincipal user, DupliDbContext db, CancellationToken ct)
    {
        var operatorUser = await LoadUserAsync(CallerId(user), db, ct);
        return await GetPreferencesAsync(operatorUser, db, ct);
    }

    private static async Task<IEnumerable<NotificationPreferenceDto>> SetMyPreferencesAsync(
        List<NotificationPreferenceDto> request, ClaimsPrincipal user, DupliDbContext db, CancellationToken ct)
    {
        var operatorUser = await LoadUserAsync(CallerId(user), db, ct);
        await SetPreferencesAsync(operatorUser.Id, request, db, ct);
        return await GetPreferencesAsync(operatorUser, db, ct);
    }

    private static async Task<OperatorUser> LoadUserAsync(Guid id, DupliDbContext db, CancellationToken ct) =>
        await db.OperatorUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User");

    /// <summary>All <see cref="AlertKind"/> values, each resolved to the stored preference or the role default.
    /// Shared with <c>UsersApi</c> (Owner editing another user's preferences).</summary>
    public static async Task<List<NotificationPreferenceDto>> GetPreferencesAsync(OperatorUser user, DupliDbContext db, CancellationToken ct)
    {
        var stored = await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == user.Id).ToDictionaryAsync(p => p.Kind, ct);
        return Enum.GetValues<AlertKind>().Select(kind =>
        {
            if (stored.TryGetValue(kind, out var preference))
                return new NotificationPreferenceDto(kind, preference.Email, preference.InApp);
            var (email, inApp) = NotificationDefaults.For(user.Role, kind);
            return new NotificationPreferenceDto(kind, email, inApp);
        }).ToList();
    }

    /// <summary>Upserts the given rows. Shared with <c>UsersApi</c>.</summary>
    public static async Task SetPreferencesAsync(Guid userId, List<NotificationPreferenceDto> request, DupliDbContext db, CancellationToken ct)
    {
        if (request is not { Count: > 0 })
            throw ApiException.BadRequest("At least one preference is required");

        var kinds = request.Select(r => r.Kind).ToList();
        var existing = await db.NotificationPreferences.Where(p => p.UserId == userId && kinds.Contains(p.Kind)).ToDictionaryAsync(p => p.Kind, ct);
        foreach (var item in request)
        {
            if (existing.TryGetValue(item.Kind, out var preference))
            {
                preference.Email = item.Email;
                preference.InApp = item.InApp;
            }
            else
            {
                db.NotificationPreferences.Add(new NotificationPreference { UserId = userId, Kind = item.Kind, Email = item.Email, InApp = item.InApp });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static NotificationDto ToDto(OperatorNotification n) =>
        new(n.Id, n.Kind, n.Event, n.Subject, n.Body, n.AgentId, n.PolicyId, n.CreatedAt, n.ReadAt);
}
