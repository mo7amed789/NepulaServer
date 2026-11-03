using Microsoft.Extensions.Logging.Abstractions;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using NebulaServer.Services.Server;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

foreach (var scenario in new[] { "normal", "stop-before-listening", "recovery-failure", "worker-failure" })
{
    using var lifetime = new TestLifetime();
    var recovery = new RecoveryBoot { Fail = scenario == "recovery-failure" };
    using var jobs = new JobQueueManager();
    using var info = new ServerInfoHostedService();
    using var coordinator = new CoreLifecycleService(lifetime, recovery, jobs, info, NullLogger<CoreLifecycleService>.Instance);
    await coordinator.StartAsync(CancellationToken.None);
    if (scenario == "stop-before-listening")
    {
        await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Check(!recovery.Called && !jobs.Started.Task.IsCompleted, "Shutdown before HTTP readiness starts no workers");
        continue;
    }
    Check(!recovery.Called, "Recovery waits for application listening event");
    lifetime.Listening.Cancel();
    if (scenario == "recovery-failure")
    {
        try { await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Failure was not observed"); }
        catch (InvalidOperationException ex) when (ex.Message == "recovery failed") { }
        Check(!jobs.Started.Task.IsCompleted, "Recovery failure propagates without starting workers");
        continue;
    }
    await Task.WhenAll(jobs.Started.Task, info.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
    Check(recovery.Called, "Recovery finishes before workers run");
    if (scenario == "worker-failure")
    {
        jobs.Failure.TrySetException(new InvalidOperationException("worker failed"));
        try { await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Failure was not observed"); }
        catch (InvalidOperationException ex) when (ex.Message == "worker failed") { }
    }
    else
    {
        await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
    }
    Check(jobs.Stopped && info.Stopped, scenario + ": coordinator awaits both service shutdowns");
}

sealed class TestLifetime : IHostApplicationLifetime, IDisposable
{
    public CancellationTokenSource Listening { get; } = new();
    public CancellationToken ApplicationStarted => Listening.Token;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
    public void Dispose() => Listening.Dispose();
}

namespace NebulaServer.Services.Core
{
    public sealed class RecoveryBoot
    {
        public bool Fail { get; init; }
        public bool Called { get; private set; }
        public Task RecoverAsync()
        {
            Called = true;
            if (Fail) throw new InvalidOperationException("recovery failed");
            return Task.CompletedTask;
        }
    }
}

namespace NebulaServer.Services
{
    public class JobQueueManager : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            Started.TrySetResult();
            try { await Failure.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { await Task.Delay(30); Stopped = true; }
        }
    }
}

namespace NebulaServer.Services.Server
{
    public sealed class ServerInfoHostedService : JobQueueManager { }
}
