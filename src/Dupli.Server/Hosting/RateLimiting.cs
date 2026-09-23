using System.Globalization;
using System.Threading.RateLimiting;
using Dupli.Server.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace Dupli.Server.Hosting;

/// <summary>Fixed window per client IP on the anonymous endpoints reachable from the internet.</summary>
public static class RateLimiting
{
    public const string AnonymousPolicy = "anonymous";

    public static IServiceCollection AddDupliRateLimiting(this IServiceCollection services, RateLimitOptions options)
    {
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(AnonymousPolicy, http => options.Enabled
                // RemoteIpAddress is already the client address from X-Forwarded-For (UseForwardedHeaders runs first).
                ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = options.PermitLimit, Window = options.Window, QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("disabled"));
        });
    }
}
