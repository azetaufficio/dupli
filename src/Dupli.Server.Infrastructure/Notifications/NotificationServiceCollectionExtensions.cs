using Dupli.Server.Domain.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Infrastructure.Notifications;

public enum NotificationChannelKind
{
    Smtp,
    Office365,
}

/// <summary>The <c>Notifications</c> section: which channel sends alert mail, plus each channel's settings.</summary>
public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    public NotificationChannelKind Channel { get; set; } = NotificationChannelKind.Smtp;
}

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddDupliNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(NotificationOptions.Section);
        var options = section.Get<NotificationOptions>() ?? new NotificationOptions();

        services.Configure<NotificationOptions>(section);
        services.Configure<SmtpOptions>(section.GetSection("Smtp"));
        services.Configure<Office365Options>(section.GetSection("Office365"));

        switch (options.Channel)
        {
            case NotificationChannelKind.Smtp:
                services.AddSingleton<INotificationChannel, SmtpNotificationChannel>();
                break;
            case NotificationChannelKind.Office365:
                services.AddSingleton<INotificationChannel>(sp => ActivatorUtilities.CreateInstance<GraphNotificationChannel>(sp,
                    (Func<Office365Options, Microsoft.Graph.GraphServiceClient>)(o => o.CreateGraphClient())));
                break;
            default:
                throw new InvalidOperationException($"Unknown {NotificationOptions.Section}:Channel '{options.Channel}'");
        }

        return services;
    }
}
