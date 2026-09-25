namespace Dupli.Agent.Core.Errors;

/// <summary>
/// Replaces known secret values with <c>***</c> in text that ends up in a <see cref="BackupException"/>, the
/// job ledger or the server (log upload, job result). Defensive: restic and pg_dump/pg_restore are not expected
/// to echo a password or key, but a misconfigured backend or a future restic version could.
/// </summary>
public static class SecretRedaction
{
    public static string Redact(string text, IEnumerable<string?> secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, "***", StringComparison.Ordinal);
        }
        return text;
    }

    public static IReadOnlyList<string> Redact(IReadOnlyList<string> lines, IEnumerable<string?> secrets)
    {
        var known = secrets.Where(s => !string.IsNullOrEmpty(s)).ToList();
        return known.Count == 0 ? lines : lines.Select(line => Redact(line, known)).ToList();
    }
}
