using NebulaServer.Services.Server;

namespace NebulaServer.Services.Core;

/// <summary>Owns recovery, worker startup, failure observation and orderly shutdown.</summary>
public sealed class CoreLifecycleService(
    IHostApplicationLifetime lifetime,
    RecoveryBoot recovery,
    JobQueueManager jobs,
    ServerInfoHostedService serverInfo,
    ILogger<CoreLifecycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => listening.TrySetResult());
        var jobsStarted = false;
        var serverInfoStarted = false;
        try
        {
            await listening.Task.WaitAsync(stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            await recovery.RecoverAsync();
            stoppingToken.ThrowIfCancellationRequested();
            await jobs.StartAsync(stoppingToken);
            jobsStarted = true;
            await serverInfo.StartAsync(stoppingToken);
            serverInfoStarted = true;
            logger.LogInformation("Recovery complete; core workers started.");

            var completed = await Task.WhenAny(jobs.ExecuteTask!, serverInfo.ExecuteTask!,
                Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken));
            if (!stoppingToken.IsCancellationRequested)
            {
                await completed;
                throw new InvalidOperationException("A core background service stopped unexpectedly.");
            }
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var shutdown = new List<Task>();
            if (jobsStarted) shutdown.Add(jobs.StopAsync(timeout.Token));
            if (serverInfoStarted) shutdown.Add(serverInfo.StopAsync(timeout.Token));
            await Task.WhenAll(shutdown);
        }
    }
}
