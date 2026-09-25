using System.Net;
using System.Net.Http.Json;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Notifications;
using Dupli.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

/// <summary>
/// Exercises <see cref="NotificationDispatcher"/> directly (fan-out from an <see cref="Alert"/> to per-user
/// <see cref="OperatorNotification"/> rows, then mail) and the <c>/api/me</c> feed it feeds.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class NotificationDispatcherTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DupliTestServer _server = new(postgres, authMode: "EntraId");

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private Task<HttpClient> OwnerAsync() => _server.SignInAsync(DupliTestServer.BootstrapOwnerEmail);

    private static async Task<string> XsrfTokenAsync(HttpClient browser, string url = "/bff/user")
    {
        var response = await browser.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(OperatorAuth.XsrfCookie + "="));
        return Uri.UnescapeDataString(cookie.Split(';')[0][(OperatorAuth.XsrfCookie.Length + 1)..]);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient browser, HttpMethod method, string url, object? body = null)
    {
        using var message = new HttpRequestMessage(method, url);
        if (body is not null)
            message.Content = JsonContent.Create(body, options: Contracts.DupliJson.Options);
        message.Headers.Add(OperatorAuth.XsrfHeader, await XsrfTokenAsync(browser));
        return await browser.SendAsync(message);
    }

    private async Task<Guid> SeedAlertAsync(AlertKind kind = AlertKind.BackupFailed, bool resolved = false)
    {
        await using var scope = _server.Services.CreateAsyncScope();
        var db = _server.Scoped<DupliDbContext>(scope);
        var now = _server.Time.GetUtcNow();
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SubjectKey = $"test:{Guid.NewGuid()}",
            Message = "test alert",
            OpenedAt = now,
            ResolvedAt = resolved ? now : null,
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        return alert.Id;
    }

    private async Task DispatchAsync()
    {
        await using var scope = _server.Services.CreateAsyncScope();
        await _server.Scoped<NotificationDispatcher>(scope).RunOnceAsync(_server.Time.GetUtcNow(), CancellationToken.None);
    }

    private async Task<List<OperatorNotification>> NotificationsAsync()
    {
        await using var scope = _server.Services.CreateAsyncScope();
        var db = _server.Scoped<DupliDbContext>(scope);
        return await db.Notifications.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Fans_out_by_role_default_and_explicit_preference()
    {
        var owner = await _server.SeedOperatorUserAsync("owner@dupli.test", OperatorRole.Owner); // default: mail + in-app
        var viewer = await _server.SeedOperatorUserAsync("viewer@dupli.test", OperatorRole.Viewer); // default: in-app only

        await SeedAlertAsync(AlertKind.BackupFailed);
        await DispatchAsync(); // fan-out and the mail send both happen in this one tick

        var rows = (await NotificationsAsync()).ToDictionary(n => n.UserId);
        Assert.True(rows[owner.Id].InApp);
        Assert.Equal(EmailStatus.Sent, rows[owner.Id].EmailStatus);
        Assert.True(rows[viewer.Id].InApp);
        Assert.Equal(EmailStatus.None, rows[viewer.Id].EmailStatus); // Viewer default: no mail

        Assert.Single(_server.Notifications.Sent);
    }

    [Fact]
    public async Task Explicit_preference_overrides_the_role_default()
    {
        var viewer = await _server.SeedOperatorUserAsync("viewer@dupli.test", OperatorRole.Viewer);
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = _server.Scoped<DupliDbContext>(scope);
            db.NotificationPreferences.Add(new NotificationPreference { UserId = viewer.Id, Kind = AlertKind.BackupFailed, Email = true, InApp = false });
            await db.SaveChangesAsync();
        }

        await SeedAlertAsync(AlertKind.BackupFailed);
        await DispatchAsync();

        var row = Assert.Single(await NotificationsAsync());
        Assert.False(row.InApp);
        Assert.Equal(EmailStatus.Sent, row.EmailStatus);
    }

    [Fact]
    public async Task Disabled_user_is_excluded_from_fan_out()
    {
        var owner = await OwnerAsync();
        var disabled = await (await SendAsync(owner, HttpMethod.Post, "/api/admin/users",
            new InviteOperatorRequest("gone@dupli.test", OperatorRole.Operator))).ReadAsync<OperatorUserDto>();
        await SendAsync(owner, HttpMethod.Post, $"/api/admin/users/{disabled.Id}/disable");

        await SeedAlertAsync(AlertKind.BackupFailed);
        await DispatchAsync();

        Assert.DoesNotContain(await NotificationsAsync(), n => n.UserId == disabled.Id);
    }

    [Fact]
    public async Task Neither_channel_on_means_no_row_at_all()
    {
        var viewer = await _server.SeedOperatorUserAsync("viewer@dupli.test", OperatorRole.Viewer);
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = _server.Scoped<DupliDbContext>(scope);
            db.NotificationPreferences.Add(new NotificationPreference { UserId = viewer.Id, Kind = AlertKind.BackupFailed, Email = false, InApp = false });
            await db.SaveChangesAsync();
        }

        await SeedAlertAsync(AlertKind.BackupFailed);
        await DispatchAsync();

        Assert.Empty(await NotificationsAsync());
    }

    [Fact]
    public async Task A_transition_is_fanned_out_only_once()
    {
        await _server.SeedOperatorUserAsync();
        await SeedAlertAsync(AlertKind.BackupFailed);

        await DispatchAsync();
        await DispatchAsync();
        await DispatchAsync();

        Assert.Single(await NotificationsAsync());
    }

    [Fact]
    public async Task Mail_is_retried_then_marked_Failed_after_the_attempt_cap()
    {
        await _server.SeedOperatorUserAsync();
        await SeedAlertAsync(AlertKind.BackupFailed);
        _server.Notifications.ThrowOnSend = true;

        for (var i = 0; i < 5; i++)
            await DispatchAsync();

        var row = Assert.Single(await NotificationsAsync());
        Assert.Equal(EmailStatus.Failed, row.EmailStatus);
        Assert.Equal(5, row.EmailAttempts);
        Assert.NotNull(row.EmailError);
    }

    [Fact]
    public async Task Unconfigured_channel_fails_pending_mail_without_retrying_forever()
    {
        await _server.SeedOperatorUserAsync();
        await SeedAlertAsync(AlertKind.BackupFailed);
        _server.Notifications.IsConfigured = false;

        await DispatchAsync();

        var row = Assert.Single(await NotificationsAsync());
        Assert.Equal(EmailStatus.Failed, row.EmailStatus);
        Assert.Equal("mail channel not configured", row.EmailError);
        Assert.Empty(_server.Notifications.Sent);
    }

    [Fact]
    public async Task Notifications_are_isolated_between_users()
    {
        var owner = await OwnerAsync();
        await SendAsync(owner, HttpMethod.Post, "/api/admin/users", new InviteOperatorRequest("viewer@dupli.test", OperatorRole.Viewer));
        var viewer = await _server.SignInAsync("viewer@dupli.test");

        await SeedAlertAsync(AlertKind.BackupFailed); // Viewer default: in-app on too, so both get a (distinct) row
        await DispatchAsync();

        var ownerFeed = await (await owner.GetAsync("/api/me/notifications")).ReadAsync<List<NotificationDto>>();
        var viewerFeed = await (await viewer.GetAsync("/api/me/notifications")).ReadAsync<List<NotificationDto>>();
        var ownerItem = Assert.Single(ownerFeed);
        var viewerItem = Assert.Single(viewerFeed);
        Assert.NotEqual(ownerItem.Id, viewerItem.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(owner, HttpMethod.Post, $"/api/me/notifications/{ownerItem.Id}/read")).StatusCode);
        // Owner cannot mark the viewer's row as read: it does not exist in the owner's feed.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(owner, HttpMethod.Post, $"/api/me/notifications/{viewerItem.Id}/read")).StatusCode);

        var viewerUnread = await (await viewer.GetAsync("/api/me/notifications/unread-count")).ReadAsync<UnreadCountDto>();
        Assert.Equal(1, viewerUnread.Count);
        var ownerUnread = await (await owner.GetAsync("/api/me/notifications/unread-count")).ReadAsync<UnreadCountDto>();
        Assert.Equal(0, ownerUnread.Count);
    }

    [Fact]
    public async Task Preferences_round_trip_and_fall_back_to_role_defaults()
    {
        var owner = await OwnerAsync();
        var initial = await (await owner.GetAsync("/api/me/notification-preferences")).ReadAsync<List<NotificationPreferenceDto>>();
        Assert.Equal(Enum.GetValues<AlertKind>().Length, initial.Count);
        Assert.All(initial, p => Assert.True(p.Email)); // Owner default

        var updated = initial.Select(p => p.Kind == AlertKind.BackupFailed ? p with { Email = false } : p).ToList();
        var saved = await (await SendAsync(owner, HttpMethod.Put, "/api/me/notification-preferences", updated))
            .ReadAsync<List<NotificationPreferenceDto>>();
        Assert.False(saved.Single(p => p.Kind == AlertKind.BackupFailed).Email);
    }

    [Fact]
    public async Task Api_key_is_forbidden_from_the_me_endpoints()
    {
        var response = await _server.Admin().GetAsync("/api/me/notifications");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
