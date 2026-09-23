namespace Dupli.Agent.Tests.Infrastructure;

/// <summary>Temporary directory deleted on dispose. Not shared with Dupli.Agent.Core.Tests
/// on purpose, to keep the two test projects independent.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "dupli-agent-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
