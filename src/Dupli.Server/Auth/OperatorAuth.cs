using System.Security.Claims;
using Dupli.Server.Configuration;
using Dupli.Server.Hosting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dupli.Server.Auth;

/// <summary>
/// Operator authentication for the web UI (BFF): the browser only ever holds an HttpOnly session cookie;
/// the OIDC exchange with Entra ID happens server-side. Unsafe requests authenticated by that cookie must
/// carry the antiforgery token (<c>XSRF-TOKEN</c> cookie echoed in the <c>X-XSRF-TOKEN</c> header, which
/// Angular's HttpClient does by default).
/// </summary>
public static class OperatorAuth
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string XsrfCookie = "XSRF-TOKEN";
    public const string XsrfHeader = "X-XSRF-TOKEN";
    public const string XsrfFormField = "__RequestVerificationToken";

    /// <summary>Claim added to principals authenticated by the admin key (no antiforgery needed: no ambient credential).</summary>
    public const string ApiKeyClaim = "dupli:api-key";

    public static void AddDupliAuthentication(this WebApplicationBuilder builder, DupliServerOptions options)
    {
        var auth = options.Auth;
        if (auth.Mode == AuthMode.Development && !(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")))
            throw new InvalidOperationException("Dupli:Auth:Mode=Development is only allowed in the Development environment");

        var authentication = builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieScheme;
            o.DefaultChallengeScheme = auth.Mode == AuthMode.EntraId ? OidcScheme : CookieScheme;
        });

        authentication.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "dupli.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = auth.SessionLifetime;
            o.SlidingExpiration = true;
            // An API call never gets an HTML redirect: the SPA handles 401/403 itself.
            o.Events.OnRedirectToLogin = ctx => Deny(ctx, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => Deny(ctx, StatusCodes.Status403Forbidden);
        });

        if (auth.Mode == AuthMode.EntraId)
        {
            var entra = auth.EntraId;
            if (string.IsNullOrWhiteSpace(entra.TenantId) || string.IsNullOrWhiteSpace(entra.ClientId) || string.IsNullOrWhiteSpace(entra.ClientSecret))
                throw new InvalidOperationException("Dupli:Auth:EntraId requires TenantId, ClientId and ClientSecret");

            authentication.AddOpenIdConnect(OidcScheme, o =>
            {
                o.SignInScheme = CookieScheme;
                o.Authority = $"{entra.Instance.TrimEnd('/')}/{entra.TenantId}/v2.0";
                o.ClientId = entra.ClientId;
                o.ClientSecret = entra.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.CallbackPath = entra.CallbackPath;
                o.SignedOutCallbackPath = entra.SignedOutCallbackPath;
                o.SaveTokens = false; // the server never calls downstream APIs on the user's behalf
                o.MapInboundClaims = false;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                o.TokenValidationParameters = new TokenValidationParameters { NameClaimType = "name", RoleClaimType = "roles" };
            });
        }

        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = XsrfHeader;
            o.FormFieldName = XsrfFormField;
            o.Cookie.Name = "dupli.af";
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.AgentPolicy, p => p
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser())
            .AddPolicy(AuthConstants.AdminPolicy, p => p
                .AddAuthenticationSchemes(AuthConstants.AdminScheme, CookieScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => IsApiKey(ctx.User) || HasRequiredRole(ctx.User, auth)));
    }

    private static Task Deny(RedirectContext<CookieAuthenticationOptions> ctx, int status)
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    public static bool IsApiKey(ClaimsPrincipal user) => user.HasClaim(c => c.Type == ApiKeyClaim);

    private static bool HasRequiredRole(ClaimsPrincipal user, AuthOptions auth) =>
        auth.Mode != AuthMode.EntraId
        || string.IsNullOrEmpty(auth.EntraId.RequiredRole)
        || user.HasClaim("roles", auth.EntraId.RequiredRole);

    /// <summary>Rejects cookie-authenticated unsafe requests without a valid antiforgery token.</summary>
    public static TBuilder RequireAntiforgeryForCookies<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !IsApiKey(http.User)
                && !await IsValidAntiforgeryAsync(http))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Missing or invalid antiforgery token");
            return await next(ctx);
        });

    private static async Task<bool> IsValidAntiforgeryAsync(HttpContext http)
    {
        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static void MapBff(this IEndpointRouteBuilder app)
    {
        var bff = app.MapGroup("/bff").AllowAnonymous();
        bff.MapGet("/login", LoginAsync).RequireRateLimiting(RateLimiting.AnonymousPolicy);
        bff.MapPost("/logout", LogoutAsync);
        bff.MapGet("/user", User);
    }

    private static async Task<IResult> LoginAsync(HttpContext http, IOptions<DupliServerOptions> options, string? returnUrl = null)
    {
        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        var auth = options.Value.Auth;
        switch (auth.Mode)
        {
            case AuthMode.EntraId:
                return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OidcScheme]);

            case AuthMode.Development:
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, auth.DevelopmentUser), new Claim("email", $"{auth.DevelopmentUser}@localhost")],
                    CookieScheme);
                await http.SignInAsync(CookieScheme, new ClaimsPrincipal(identity));
                return Results.Redirect(target);

            default:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Interactive login is not configured (Dupli:Auth:Mode)");
        }
    }

    /// <summary>Plain form POST from the SPA (so the browser can follow the redirect to Entra ID's end-session page).</summary>
    private static async Task<IResult> LogoutAsync(HttpContext http, IAntiforgery antiforgery, IOptions<DupliServerOptions> options)
    {
        if (!await IsValidAntiforgeryAsync(http))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Missing or invalid antiforgery token");

        var properties = new AuthenticationProperties { RedirectUri = "/" };
        return options.Value.Auth.Mode == AuthMode.EntraId && http.User.Identity?.IsAuthenticated == true
            ? Results.SignOut(properties, [CookieScheme, OidcScheme])
            : Results.SignOut(properties, [CookieScheme]);
    }

    /// <summary>Who is signed in. Also (re)issues the antiforgery token bound to the current session.</summary>
    private static UserInfoDto User(HttpContext http, IAntiforgery antiforgery, IOptions<DupliServerOptions> options)
    {
        var auth = options.Value.Auth;
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(XsrfCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // read by Angular and echoed in X-XSRF-TOKEN
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });

        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
            return new UserInfoDto(false, auth.Mode.ToString(), null, null, false);

        return new UserInfoDto(
            true,
            auth.Mode.ToString(),
            user.Identity.Name ?? user.FindFirstValue("preferred_username"),
            user.FindFirstValue("email") ?? user.FindFirstValue("preferred_username"),
            HasRequiredRole(user, auth));
    }

    private static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}

public sealed record UserInfoDto(bool Authenticated, string Mode, string? Name, string? Email, bool Authorized);
