using System.Diagnostics;

namespace Dupli.Agent.Launcher;

/// <summary>The agent process started by the Launcher.</summary>
public interface IChildProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code.</summary>
    Task<int> Exited { get; }

    /// <summary>Asks for a graceful stop by closing the child's stdin.</summary>
    void RequestStop();

    void Kill();
}

public interface IChildProcessFactory
{
    IChildProcess Start(string exePath, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment);
}

/// <summary>
/// Real child process. stdin is a pipe owned by the Launcher: closing it is the stop signal, and it also closes
/// when the Launcher dies, so the agent never outlives its supervisor. stdout/stderr are inherited (the agent
/// logs to its own files).
/// </summary>
public sealed class ChildProcessFactory : IChildProcessFactory
{
    public IChildProcess Start(string exePath, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exePath}");
        return new Child(process);
    }

    private sealed class Child : IChildProcess
    {
        private readonly Process _process;

        public Child(Process process)
        {
            _process = process;
            Id = process.Id;
            Exited = WaitAsync();
        }

        public int Id { get; }
        public Task<int> Exited { get; }

        private async Task<int> WaitAsync()
        {
            await _process.WaitForExitAsync();
            return _process.ExitCode;
        }

        public void RequestStop()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Already exited.
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
