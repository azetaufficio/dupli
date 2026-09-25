using System.Security.Claims;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Hosting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dupli.Server.Auth;

/// <summary>
/// Operator authentication for the web UI (BFF): the browser only ever holds an HttpOnly session cookie;
/// the OIDC exchange with Entra ID happens server-side. Who may use Dupli, and with which role, is the
/// <c>operator_user</c> table (<see cref="OperatorDirectory"/>), checked again on every request.
/// Unsafe requests authenticated by a cookie must carry the antiforgery token (<c>XSRF-TOKEN</c> cookie
/// echoed in the <c>X-XSRF-TOKEN</c> header, which Angular's HttpClient does by default).
/// </summary>
public static class OperatorAuth
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>Short session opened on the <c>/admin</c> page with the admin key: user management only.</summary>
    public const string BreakGlassScheme = "BreakGlass";
    public static readonly TimeSpan BreakGlassLifetime = TimeSpan.FromMinutes(15);

    public const string XsrfCookie = "XSRF-TOKEN";
    public const string XsrfHeader = "X-XSRF-TOKEN";
    public const string XsrfFormField = "__RequestVerificationToken";
    public const string AccessDeniedPath = "/access-denied";

    /// <summary>Claim added to principals authenticated by the admin key (no antiforgery needed: no ambient credential).</summary>
    public const string ApiKeyClaim = "dupli:api-key";
    public const string BreakGlassClaim = "dupli:break-glass";

    public static void AddDupliAuthentication(this WebApplicationBuilder builder, DupliServerOptions options)
    {
        var auth = options.Auth;
        var breakGlassEnabled = !string.IsNullOrEmpty(options.Admin.ApiKey);
        builder.Services.AddScoped<OperatorDirectory>();

        var authentication = builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieScheme;
            o.DefaultChallengeScheme = auth.Mode == AuthMode.EntraId ? OidcScheme : CookieScheme;
        });

        authentication.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "dupli.session";
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = auth.SessionLifetime;
            o.SlidingExpiration = true;
            // An API call never gets an HTML redirect: the SPA handles 401/403 itself.
            o.Events.OnRedirectToLogin = ctx => Deny(ctx, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => Deny(ctx, StatusCodes.Status403Forbidden);
            o.Events.OnValidatePrincipal = ValidateOperatorAsync;
        });

        authentication.AddCookie(BreakGlassScheme, o =>
        {
            o.Cookie.Name = "dupli.admin";
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = BreakGlassLifetime;
            o.SlidingExpiration = false;
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
                o.TokenValidationParameters = new TokenValidationParameters { NameClaimType = OperatorClaims.Name };
                o.Events.OnTokenValidated = OnTokenValidatedAsync;
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
            .AddPolicy(AuthConstants.ViewerPolicy, RolePolicy(OperatorRole.Viewer))
            .AddPolicy(AuthConstants.OperatorPolicy, RolePolicy(OperatorRole.Operator))
            .AddPolicy(AuthConstants.OwnerPolicy, RolePolicy(OperatorRole.Owner))
            .AddPolicy(AuthConstants.UsersPolicy, p => p
                .AddAuthenticationSchemes(AuthConstants.AdminScheme, CookieScheme, BreakGlassScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => IsApiKey(ctx.User) || HasRole(ctx.User, OperatorRole.Owner)
                                         || (breakGlassEnabled && IsBreakGlass(ctx.User))))
            // Same schemes as UsersPolicy (so the admin key/break-glass can reach the endpoint and be refused
            // with 403 inside it, not redirected/401'd by authentication); any signed-in operator role qualifies.
            .AddPolicy(AuthConstants.MePolicy, p => p
                .AddAuthenticationSchemes(AuthConstants.AdminScheme, CookieScheme, BreakGlassScheme)
                .RequireAuthenticatedUser())
            // Same schemes, in the same order, as UsersPolicy: the antiforgery token issued here must match there.
            .AddPolicy(AuthConstants.BreakGlassPolicy, p => p
                .AddAuthenticationSchemes(CookieScheme, BreakGlassScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => breakGlassEnabled && IsBreakGlass(ctx.User)));
    }

    private static Action<AuthorizationPolicyBuilder> RolePolicy(OperatorRole minimum) => p => p
        .AddAuthenticationSchemes(AuthConstants.AdminScheme, CookieScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => IsApiKey(ctx.User) || HasRole(ctx.User, minimum));

    private static Task Deny(RedirectContext<CookieAuthenticationOptions> ctx, int status)
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    public static bool IsApiKey(ClaimsPrincipal user) => user.HasClaim(c => c.Type == ApiKeyClaim);

    public static bool IsBreakGlass(ClaimsPrincipal user) => user.HasClaim(c => c.Type == BreakGlassClaim);

    public static bool HasRole(ClaimsPrincipal user, OperatorRole minimum) => OperatorClaims.RoleOf(user) >= minimum;

    /// <summary>Who did it, for <c>created_by</c>/<c>updated_by</c> and logs.</summary>
    public static string Actor(ClaimsPrincipal user) =>
        IsApiKey(user) ? "admin-key"
        : user.FindFirstValue(OperatorClaims.Email) is { } email ? email
        : IsBreakGlass(user) ? "break-glass"
        : "unknown";

    /// <summary>Entra ID sign-in: only people in the operator table get a session cookie.</summary>
    private static async Task OnTokenValidatedAsync(Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext ctx)
    {
        var identity = ctx.Principal is { } principal ? OperatorIdentity.FromClaims(principal) : null;
        var session = identity is null
            ? null
            : await ctx.HttpContext.RequestServices.GetRequiredService<OperatorDirectory>().SignInAsync(identity, ctx.HttpContext.RequestAborted);
        if (session is null)
        {
            ctx.HandleResponse();
            ctx.Response.Redirect(AccessDeniedUrl(identity?.Shown));
            return;
        }
        ctx.Principal = session;
    }

    public static string AccessDeniedUrl(string? email) =>
        string.IsNullOrEmpty(email) ? AccessDeniedPath : $"{AccessDeniedPath}?email={Uri.EscapeDataString(email)}";

    /// <summary>Re-reads the operator on every request: removed or disabled users lose the session, role changes apply at once.</summary>
    private static async Task ValidateOperatorAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var user = principal is not null && OperatorClaims.UserIdOf(principal) is { } id
            ? await ctx.HttpContext.RequestServices.GetRequiredService<OperatorDirectory>().GetAsync(id, ctx.HttpContext.RequestAborted)
            : null;
        if (principal is null || user is null || !user.IsActive || user.ObjectId != principal.FindFirstValue(OperatorClaims.ObjectId))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieScheme);
            return;
        }

        var identity = new ClaimsIdentity(
            principal.Claims.Where(c => c.Type != OperatorClaims.Role),
            principal.Identity?.AuthenticationType, OperatorClaims.Name, OperatorClaims.Role);
        identity.AddClaim(new Claim(OperatorClaims.Role, user.Role.ToString()));
        ctx.ReplacePrincipal(new ClaimsPrincipal(identity));
    }

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
        bff.MapGet("/login", Login).RequireRateLimiting(RateLimiting.AnonymousPolicy);
        bff.MapPost("/logout", LogoutAsync);
        bff.MapGet("/user", User);

        // GET only tells the /admin page whether break-glass is available (404 without an admin key).
        bff.MapGet("/admin-login", (IOptions<DupliServerOptions> options) =>
            string.IsNullOrEmpty(options.Value.Admin.ApiKey) ? Results.NotFound() : Results.NoContent());
        bff.MapPost("/admin-login", BreakGlassLoginAsync).RequireRateLimiting(RateLimiting.AnonymousPolicy);
        bff.MapPost("/admin-logout", BreakGlassLogoutAsync);
        // Outside the group: its AllowAnonymous would override the policy.
        app.MapGet("/bff/admin-session", BreakGlassSession).RequireAuthorization(AuthConstants.BreakGlassPolicy);
    }

    /// <param name="selectAccount">From the access-denied page: let the user pick another Entra ID account.</param>
    private static IResult Login(IOptions<DupliServerOptions> options, string? returnUrl = null, bool selectAccount = false)
    {
        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        if (options.Value.Auth.Mode != AuthMode.EntraId)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Interactive login is not configured (Dupli:Auth:Mode)");

        var properties = new OpenIdConnectChallengeProperties { RedirectUri = target };
        if (selectAccount)
            properties.Prompt = "select_account";
        return Results.Challenge(properties, [OidcScheme]);
    }

    /// <summary>Plain form POST from the SPA (so the browser can follow the redirect to Entra ID's end-session page).</summary>
    private static async Task<IResult> LogoutAsync(HttpContext http, IOptions<DupliServerOptions> options)
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
        IssueXsrfToken(http, antiforgery);
        var mode = options.Value.Auth.Mode.ToString();
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
            return new UserInfoDto(false, mode, null, null, null);

        return new UserInfoDto(true, mode, user.Identity.Name, user.FindFirstValue(OperatorClaims.Email), OperatorClaims.RoleOf(user));
    }

    private static void IssueXsrfToken(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(XsrfCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // read by Angular and echoed in X-XSRF-TOKEN
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
    }

    /// <summary>
    /// <c>/admin</c> page: the admin key, sent once, opens a short non-sliding session limited to user management
    /// (recovery when no owner can sign in any more). The key itself never stays in the browser. JSON body only,
    /// so a cross-site form cannot post it.
    /// </summary>
    private static async Task<IResult> BreakGlassLoginAsync(
        BreakGlassLoginRequest request, HttpContext http, IOptions<DupliServerOptions> options, ILogger<BreakGlassLoginRequest> logger)
    {
        var expected = options.Value.Admin.ApiKey;
        if (string.IsNullOrEmpty(expected))
            return Results.NotFound();

        if (string.IsNullOrEmpty(request.Key) || !AdminApiKeyHandler.Matches(request.Key, expected))
        {
            logger.LogWarning("Break-glass login refused from {RemoteIp}", http.Connection.RemoteIpAddress);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Invalid admin key");
        }

        var identity = new ClaimsIdentity(
            [new Claim(OperatorClaims.UserId, "break-glass"), new Claim(OperatorClaims.Name, "break-glass"), new Claim(BreakGlassClaim, "true")],
            BreakGlassScheme, OperatorClaims.Name, OperatorClaims.Role);
        await http.SignInAsync(BreakGlassScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
        logger.LogWarning("Break-glass session opened from {RemoteIp}", http.Connection.RemoteIpAddress);
        return Results.NoContent();
    }

    /// <summary>The break-glass session is open. Issues the antiforgery token bound to it.</summary>
    private static BreakGlassSessionDto BreakGlassSession(HttpContext http, IAntiforgery antiforgery)
    {
        IssueXsrfToken(http, antiforgery);
        return new BreakGlassSessionDto(true);
    }

    private static async Task BreakGlassLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(BreakGlassScheme);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}

public sealed record UserInfoDto(bool Authenticated, string Mode, string? Name, string? Email, OperatorRole? Role);

public sealed record BreakGlassLoginRequest(string? Key);

public sealed record BreakGlassSessionDto(bool Active);
