using Dupli.Agent.Cli;
using Dupli.Agent.Logging;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Scheduling;

/// <summary>
/// Local M1 scheduler: evaluates each policy's cron+timezone independently and runs it when due.
/// The server-side scheduler (M2) replaces this for fleet-wide coordination.
/// </summary>
public sealed class PolicyScheduler(AgentRuntime runtime, TimeProvider time, ILogger<PolicyScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedules = runtime.Config.Policies
            .Select(p => (Policy: p, Expression: CronExpression.Parse(p.Cron), Zone: ResolveTimeZone(p.TimeZone)))
            .ToList();

        var nextRun = schedules.ToDictionary(s => s.Policy.Policy.PolicyId, _ => (DateTimeOffset?)null);

        logger.LogInformation("Scheduler started with {Count} policies", schedules.Count);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            foreach (var schedule in schedules)
            {
                var policyId = schedule.Policy.Policy.PolicyId;
                nextRun[policyId] ??= schedule.Expression.GetNextOccurrence(now.UtcDateTime, schedule.Zone) is { } next
                    ? new DateTimeOffset(next, TimeSpan.Zero)
                    : null;

                if (nextRun[policyId] is { } due && due <= now)
                {
                    await RunAsync(schedule.Policy.Policy.PolicyId, stoppingToken);
                    nextRun[policyId] = schedule.Expression.GetNextOccurrence(now.UtcDateTime, schedule.Zone) is { } after
                        ? new DateTimeOffset(after, TimeSpan.Zero)
                        : null;
                }
            }

            try
            {
                await Task.Delay(PollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunAsync(string policyId, CancellationToken cancellationToken)
    {
        var policy = runtime.Config.Policies.First(p => p.Policy.PolicyId == policyId).Policy;
        using var _ = SerilogSetup.PushRun($"backup:{policyId}");
        try
        {
            var result = await runtime.PolicyRunner.RunAsync(policy, runtime.Repository, runtime.Config.Host, cancellationToken);
            logger.LogInformation("Scheduled run of {PolicyId} finished: {Status}", policyId, result.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled run of {PolicyId} threw unexpectedly", policyId);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
