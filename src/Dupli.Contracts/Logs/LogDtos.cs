namespace Dupli.Contracts.Logs;

/// <summary>Batch of Information+ log events. Full logs stay on the agent.</summary>
public sealed record AgentLogBatchDto
{
    public required IReadOnlyList<AgentLogEntryDto> Entries { get; init; }

    /// <summary>Events dropped locally because the upload buffer was full.</summary>
    public int Dropped { get; init; }
}

public sealed record AgentLogEntryDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? Exception { get; init; }
    public string? JobId { get; init; }
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}
