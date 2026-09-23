using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Core.Processes;

public sealed class ProcessSpec
{
    /// <summary>Absolute path of the executable. Never resolved through PATH.</summary>
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment variables. Values are never logged.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public string? WorkingDirectory { get; init; }
}

public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> StderrTail);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private const int StderrTailLines = 50;

    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(spec.FileName))
            throw new ArgumentException($"Executable path must be absolute: {spec.FileName}", nameof(spec));

        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FileName)!,
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in spec.Environment)
            psi.Environment[key] = value;

        // Arguments carry no secrets by design (secrets go through Environment).
        logger.LogDebug("Starting {FileName} {Arguments}", spec.FileName, string.Join(' ', spec.Arguments));

        using var process = new Process { StartInfo = psi };
        var stderrTail = new Queue<string>(StderrTailLines);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {spec.FileName}");

        var stdoutTask = PumpAsync(process.StandardOutput, line => onStdoutLine?.Invoke(line));
        var stderrTask = PumpAsync(process.StandardError, line =>
        {
            logger.LogDebug("[{Tool}] {Line}", Path.GetFileNameWithoutExtension(spec.FileName), line);
            lock (stderrTail)
            {
                if (stderrTail.Count == StderrTailLines)
                    stderrTail.Dequeue();
                stderrTail.Enqueue(line);
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Cancellation requested, killing {FileName} (pid {Pid})", spec.FileName, process.Id);
            TryKill(process);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        lock (stderrTail)
            return new ProcessResult(process.ExitCode, stderrTail.ToArray());
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
            onLine(line);
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to kill process {Pid}", process.Id);
        }
    }
}
