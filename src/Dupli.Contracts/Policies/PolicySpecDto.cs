using System.Text.Json.Serialization;

namespace Dupli.Contracts.Policies;

/// <summary>
/// Fully resolved backup policy as executed by the agent. Never contains secrets:
/// credentials are referenced by name and resolved from the agent's local secret store.
/// </summary>
public sealed record PolicySpecDto
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<BackupSourceDto> Sources { get; init; }
    public RetentionDto Retention { get; init; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DirectorySourceDto), "directory")]
[JsonDerivedType(typeof(PostgresSourceDto), "postgres")]
public abstract record BackupSourceDto
{
    public required string SourceId { get; init; }
}

public sealed record DirectorySourceDto : BackupSourceDto
{
    public required IReadOnlyList<string> Paths { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = [];
}

public sealed record PostgresSourceDto : BackupSourceDto
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public required string Username { get; init; }

    /// <summary>Name of the secret holding the password in the agent secret store.</summary>
    public required string PasswordSecret { get; init; }

    public DatabaseSelection DatabaseSelection { get; init; } = DatabaseSelection.AllExcept;

    /// <summary><see cref="DatabaseSelection.AllExcept"/>: databases never dumped, in addition to templates and <c>postgres</c>.</summary>
    public IReadOnlyList<string> ExcludeDatabases { get; init; } = [];

    /// <summary><see cref="DatabaseSelection.Only"/>: the databases dumped (<c>postgres</c> allowed). A missing one fails the source.</summary>
    public IReadOnlyList<string> IncludeDatabases { get; init; } = [];

    public bool IncludeGlobals { get; init; } = true;

    /// <summary>Optional explicit bin directory (containing pg_dump). Auto-detected when null.</summary>
    public string? BinDirectory { get; init; }
}

public enum DatabaseSelection
{
    /// <summary>Every connectable non-template database except <c>postgres</c> and <see cref="PostgresSourceDto.ExcludeDatabases"/>.</summary>
    AllExcept,

    /// <summary>Exactly <see cref="PostgresSourceDto.IncludeDatabases"/>.</summary>
    Only,
}

public sealed record RetentionDto
{
    public int KeepDaily { get; init; } = 7;
    public int KeepWeekly { get; init; } = 4;
    public int KeepMonthly { get; init; } = 12;
}
