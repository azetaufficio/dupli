using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Restic;
using Dupli.Agent.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Core.Tests;

/// <summary>
/// Known secret values (repository password, S3 keys) never reach a <see cref="BackupException"/> message,
/// even if restic's stderr echoed one back verbatim: <see cref="ResticBackupEngine"/> redacts the stderr tail
/// before <c>ResticErrors.FromExitCode</c> builds the exception. A stub <see cref="IProcessRunner"/>, no real
/// restic binary needed.
/// </summary>
public sealed class ResticBackupEngineRedactionTests
{
    private sealed class StubProcessRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onStdoutLine, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    [Fact]
    public async Task Stderr_secrets_are_redacted_before_the_exception_is_created()
    {
        const string password = "s3kr3t-restic-password";
        const string accessKey = "AKIA-TEST";
        const string secretKey = "wJalrXUtnFEMI-secret";
        var stderr = new[] { $"fatal: repository initialization failed for password {password} with key {accessKey}/{secretKey}" };
        var runner = new StubProcessRunner(new ProcessResult(ResticExitCodes.Fatal, stderr));
        var engine = new ResticBackupEngine(new FixedResticBinaryProvider("/bin/restic"), runner, NullLogger<ResticBackupEngine>.Instance);
        var repo = new RepositoryTarget("s3:https://example.test/bucket", password,
            new Dictionary<string, string> { ["AWS_ACCESS_KEY_ID"] = accessKey, ["AWS_SECRET_ACCESS_KEY"] = secretKey });

        var ex = await Assert.ThrowsAsync<BackupException>(() => engine.UnlockStaleAsync(repo, CancellationToken.None));

        Assert.DoesNotContain(password, ex.Message);
        Assert.DoesNotContain(accessKey, ex.Message);
        Assert.DoesNotContain(secretKey, ex.Message);
        Assert.Contains("***", ex.Message);
    }
}
