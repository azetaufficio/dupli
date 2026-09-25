using Dupli.Contracts.Jobs;

namespace Dupli.Agent.Core.Secrets;

/// <summary>
/// Just-in-time credentials for exactly one job (<c>POST api/agents/jobs/{jobId}/credentials</c>): the
/// repository password/backend keys and the PostgreSQL passwords the job's sources need. Held only in memory
/// for the lifetime of the job and released by <see cref="Dispose"/> when it ends; never written to disk,
/// a log, the job ledger or a command line.
/// </summary>
/// <remarks>Read-only <see cref="ISecretStore"/>: <see cref="Set"/> always throws, so the agent cannot
/// accidentally persist one of these values through the interface.</remarks>
public sealed class JobCredentials : ISecretStore, IDisposable
{
    private string? _repositoryPassword;
    private string? _accessKeyId;
    private string? _secretAccessKey;
    private string? _sessionToken;
    private Dictionary<string, string>? _postgres;

    public JobCredentials(JobCredentialsResponse response)
    {
        _repositoryPassword = response.Repository.Password;
        _accessKeyId = response.Repository.AccessKeyId;
        _secretAccessKey = response.Repository.SecretAccessKey;
        _sessionToken = response.Repository.SessionToken;
        _postgres = new Dictionary<string, string>(response.Postgres, StringComparer.Ordinal);
    }

    public string? RepositoryPassword => _repositoryPassword;
    public string? AccessKeyId => _accessKeyId;
    public string? SecretAccessKey => _secretAccessKey;
    public string? SessionToken => _sessionToken;

    /// <summary>The only thing looked up by name: a PostgreSQL source's <c>PasswordSecret</c>.</summary>
    public string? Get(string name) => _postgres?.GetValueOrDefault(name);

    public void Set(string name, string value) =>
        throw new NotSupportedException("Job credentials are read-only: the agent never writes a secret back");

    /// <summary>
    /// Drops every reference so the values become unreachable. .NET strings cannot be zeroed in place;
    /// this is the closest thing to "release" available for managed strings.
    /// </summary>
    public void Dispose()
    {
        _repositoryPassword = null;
        _accessKeyId = null;
        _secretAccessKey = null;
        _sessionToken = null;
        _postgres?.Clear();
        _postgres = null;
    }

    public override string ToString() => "JobCredentials";
}
