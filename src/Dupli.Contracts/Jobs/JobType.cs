namespace Dupli.Contracts.Jobs;

/// <summary>
/// The only job kinds an agent executes. There is intentionally no generic "command" job.
/// </summary>
public enum JobType
{
    Backup,
    Restore,
    RestoreTest,
    RepositoryCheck,
    Retention,
    RestartAgent,
    AgentUpdate,
    ResticUpdate,
}
