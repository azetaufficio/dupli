using System.Security.Claims;
using System.Security.Cryptography;
using Dupli.Server.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dupli.Server.Auth;

public static class AuthConstants
{
    public const string AgentPolicy = "Agent";

    /// <summary>Operator API, read-only: every signed-in operator (and the admin key).</summary>
    public const string ViewerPolicy = "Viewer";

    /// <summary>Operator API writes and snapshot contents: Operator or Owner.</summary>
    public const string OperatorPolicy = "Operator";

    /// <summary>Storage target writes and releases: Owner only.</summary>
    public const string OwnerPolicy = "Owner";

    /// <summary>Operator user management: Owner, or a break-glass session opened with the admin key.</summary>
    public const string UsersPolicy = "Users";

    /// <summary>The signed-in operator's own notifications: any role, but not the admin key or break-glass
    /// (neither has an <see cref="OperatorClaims.UserId"/> to own a feed).</summary>
    public const string MePolicy = "Me";

    /// <summary>Only a break-glass session (the <c>/admin</c> page).</summary>
    public const string BreakGlassPolicy = "BreakGlass";

    public const string AdminScheme = "AdminKey";
    public const string AdminKeyHeader = "X-Dupli-Admin-Key";
    public const string Issuer = "dupli-server";
    public const string AgentAudience = "dupli-agent";
}

/// <summary>HMAC key signing agent JWTs: configured, or random per process.</summary>
public sealed class AgentSigningKey(IOptions<DupliServerOptions> options)
{
    public SymmetricSecurityKey Key { get; } = new(
        string.IsNullOrWhiteSpace(options.Value.Agents.SigningKey)
            ? RandomNumberGenerator.GetBytes(32)
            : Convert.FromBase64String(options.Value.Agents.SigningKey));
}

/// <summary>Uses the real clock on purpose: JWT validation does too, independently of the injected TimeProvider.</summary>
public sealed class AgentTokenIssuer(AgentSigningKey key, IOptions<DupliServerOptions> options)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid agentId)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now + options.Value.Agents.TokenLifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthConstants.Issuer,
            Audience = AuthConstants.AgentAudience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, agentId.ToString())]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key.Key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>Agent id from the JWT <c>sub</c> claim.</summary>
    public static Guid AgentId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Agent token without subject"));
}
