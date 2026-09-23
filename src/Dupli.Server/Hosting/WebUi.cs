using Dupli.Server.Configuration;

namespace Dupli.Server.Hosting;

/// <summary>Serves the Angular build (wwwroot) with client-side routing, plus browser hardening headers.</summary>
public static class WebUi
{
    public static void MapWebUi(this IEndpointRouteBuilder app)
    {
        // Unknown API/BFF paths are real 404s, never the SPA shell.
        app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallback("/bff/{**rest}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, DupliServerOptions options)
    {
        // The logout form post redirects to Entra ID's end-session endpoint: form-action must allow it.
        var identityProvider = options.Auth.Mode == AuthMode.EntraId
            ? " " + new Uri(options.Auth.EntraId.Instance).GetLeftPart(UriPartial.Authority)
            : "";
        var csp = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
                  $"connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'{identityProvider}";

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers.ContentSecurityPolicy = csp;
            await next(context);
        });
    }
}
