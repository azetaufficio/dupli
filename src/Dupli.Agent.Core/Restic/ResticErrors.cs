using Dupli.Agent.Core.Errors;

namespace Dupli.Agent.Core.Restic;

/// <summary>restic exit codes, see https://restic.readthedocs.io/en/stable/075_scripting.html</summary>
public static class ResticExitCodes
{
    public const int Success = 0;
    public const int Fatal = 1;
    public const int RuntimeError = 2;
    public const int IncompleteSnapshot = 3;
    public const int RepositoryMissing = 10;
    public const int LockFailed = 11;
    public const int WrongPassword = 12;
    public const int Interrupted = 130;
}

public static class ResticErrors
{
    // restic retries backend calls internally; if it still fails with these, a later retry may succeed.
    private static readonly string[] TransientMarkers =
    [
        "connection refused",
        "connection reset",
        "no such host",
        "i/o timeout",
        "timeout",
        "tls handshake",
        "temporary failure",
        "service unavailable",
        "503",
        "502",
        "504",
        "slowdown",
        "too many requests",
        "eof",
    ];

    public static BackupException FromExitCode(string operation, int exitCode, IReadOnlyList<string> stderr)
    {
        var detail = string.Join(Environment.NewLine, stderr.TakeLast(10)).Trim();
        var message = $"restic {operation} failed (exit {exitCode}): {detail}";

        return exitCode switch
        {
            ResticExitCodes.WrongPassword => BackupException.Permanent($"restic {operation}: wrong repository password"),
            ResticExitCodes.RepositoryMissing => BackupException.Permanent($"restic {operation}: repository does not exist"),
            ResticExitCodes.LockFailed => BackupException.Transient(message),
            _ when LooksTransient(detail) => BackupException.Transient(message),
            _ => BackupException.Permanent(message),
        };
    }

    public static bool LooksTransient(string text)
    {
        var lower = text.ToLowerInvariant();
        return TransientMarkers.Any(lower.Contains);
    }
}
