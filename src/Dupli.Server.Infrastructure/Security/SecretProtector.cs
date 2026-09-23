using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Dupli.Server.Infrastructure.Security;

/// <summary>
/// Escrow encryption for secrets kept in the database (repository passwords, S3 keys).
/// Keys live in the Data Protection key ring, mounted outside the database volume.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Dupli.Escrow.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

public static class SecretHashing
{
    /// <summary>Random URL-safe secret with 256 bits of entropy.</summary>
    public static string NewSecret(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 hex. Adequate for high-entropy random secrets (not for passwords chosen by humans).</summary>
    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool Verify(string secret, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(expectedHash));
}
