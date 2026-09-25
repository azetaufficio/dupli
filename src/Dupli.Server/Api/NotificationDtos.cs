using Dupli.Server.Domain.Monitoring;

namespace Dupli.Server.Api;

// The signed-in operator's notification feed (/api/me) and per-user preferences, also used by Owners to
// edit another user's preferences (/api/admin/users/{id}/notification-preferences).

public sealed record NotificationDto(
    Guid Id,
    AlertKind Kind,
    NotificationEvent Event,
    string Subject,
    string Body,
    Guid? AgentId,
    Guid? PolicyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record UnreadCountDto(int Count);

public sealed record NotificationPreferenceDto(AlertKind Kind, bool Email, bool InApp);
