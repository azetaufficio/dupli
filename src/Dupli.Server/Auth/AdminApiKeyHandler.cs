using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dupli.Server.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Auth;

/// <summary>
/// Admin API authenticated by a shared key header, for automation. Operators sign in through the BFF
/// (<see cref="OperatorAuth"/>). Disabled (every request unauthenticated) when no key is configured.
/// </summary>
public sealed class AdminApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<DupliServerOptions> options)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = options.Value.Admin.ApiKey;
        if (string.IsNullOrEmpty(expected) || !Request.Headers.TryGetValue(AuthConstants.AdminKeyHeader, out var provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided.ToString()), Encoding.UTF8.GetBytes(expected)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin key"));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin-key"), new Claim(OperatorAuth.ApiKeyClaim, "true")], AuthConstants.AdminScheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthConstants.AdminScheme)));
    }
}
