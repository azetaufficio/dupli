namespace Dupli.Server.Background;

/// <summary>A unit of periodic background work, resolved in its own DI scope each tick (testable in isolation).</summary>
public interface IPeriodicTask
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

/// <summary>Runs <typeparamref name="TTask"/> every <paramref name="interval"/>; a failing tick is logged, never fatal.</summary>
public sealed class PeriodicWorker<TTask>(
    IServiceScopeFactory scopes,
    TimeSpan interval,
    TimeProvider time,
    ILogger<PeriodicWorker<TTask>> logger) : BackgroundService
    where TTask : IPeriodicTask
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TTask>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "{Task} tick failed", typeof(TTask).Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public static class PeriodicWorkerExtensions
{
    public static IServiceCollection AddPeriodicTask<TTask>(this IServiceCollection services, Func<IServiceProvider, TimeSpan> interval, bool runWorker)
        where TTask : class, IPeriodicTask
    {
        services.AddScoped<TTask>();
        if (runWorker)
        {
            services.AddHostedService(sp => new PeriodicWorker<TTask>(
                sp.GetRequiredService<IServiceScopeFactory>(),
                interval(sp),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<PeriodicWorker<TTask>>>()));
        }
        return services;
    }
}
