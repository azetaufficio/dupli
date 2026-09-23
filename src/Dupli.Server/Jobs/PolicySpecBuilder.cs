using System.Text.Json;
using Dupli.Contracts;
using Dupli.Contracts.Policies;
using Dupli.Server.Domain.Policies;

namespace Dupli.Server.Jobs;

/// <summary>Resolves a stored policy into the self-contained spec sent to the agent.</summary>
public static class PolicySpecBuilder
{
    public static PolicySpecDto Build(BackupPolicy policy) => new()
    {
        PolicyId = policy.Id.ToString(),
        Name = policy.Name,
        Sources = policy.Sources.OrderBy(s => s.SourceKey, StringComparer.Ordinal).Select(ToDto).ToList(),
        Retention = Retention(policy),
    };

    public static RetentionDto Retention(BackupPolicy policy) => new()
    {
        KeepDaily = policy.KeepDaily,
        KeepWeekly = policy.KeepWeekly,
        KeepMonthly = policy.KeepMonthly,
    };

    public static BackupSourceDto ToDto(BackupSource source) =>
        (JsonSerializer.Deserialize<BackupSourceDto>(source.Spec, DupliJson.Options)
            ?? throw new InvalidOperationException($"Source {source.Id} has an empty spec"))
        with { SourceId = source.SourceKey };

    public static string Serialize(BackupSourceDto source) =>
        JsonSerializer.Serialize(source, DupliJson.Options);

    public static BackupSourceType TypeOf(BackupSourceDto source) => source switch
    {
        DirectorySourceDto => BackupSourceType.Directory,
        PostgresSourceDto => BackupSourceType.PostgreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.GetType().Name, "Unknown source type"),
    };
}
