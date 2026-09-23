namespace Dupli.Agent.Core.Errors;

public enum ErrorKind
{
    /// <summary>May succeed if retried (network, S3 hiccup, repository lock held).</summary>
    Transient,

    /// <summary>Retrying will not help (wrong password, missing directory, bad config).</summary>
    Permanent,
}

public class BackupException(ErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ErrorKind Kind { get; } = kind;

    public static BackupException Transient(string message, Exception? inner = null) =>
        new(ErrorKind.Transient, message, inner);

    public static BackupException Permanent(string message, Exception? inner = null) =>
        new(ErrorKind.Permanent, message, inner);
}
