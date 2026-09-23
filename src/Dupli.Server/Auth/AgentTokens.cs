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
    public const string AdminPolicy = "Admin";
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
