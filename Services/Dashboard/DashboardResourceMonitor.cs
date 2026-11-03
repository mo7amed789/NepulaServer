using System.Diagnostics;

namespace NebulaServer.Services.Dashboard;

/// <summary>
/// Shares inexpensive, coherent telemetry across dashboard clients. Job state is
/// still read on each request; only OS resource queries are sampled and cached.
/// </summary>
internal sealed class DashboardResourceMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DriveInfo _drive;
    private long _lastProcessSample = Stopwatch.GetTimestamp();
    private long _lastStorageSample;
    private TimeSpan _lastCpuTime;
    private ResourceSnapshot _snapshot;

    public DashboardResourceMonitor(string downloadsPath)
    {
        _drive = new DriveInfo(Path.GetPathRoot(downloadsPath)!);
        _lastCpuTime = _process.TotalProcessorTime;
        _snapshot = new(0, _process.WorkingSet64, 0, 0);
    }

    public ResourceSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastProcessSample, now);
            if (elapsed >= TimeSpan.FromMilliseconds(500))
            {
                _process.Refresh();
                var cpuTime = _process.TotalProcessorTime;
                var cpuUsage = (cpuTime - _lastCpuTime).TotalMilliseconds /
                    (Environment.ProcessorCount * elapsed.TotalMilliseconds) * 100;
                _snapshot = _snapshot with
                {
                    CpuUsage = Math.Round(Math.Clamp(cpuUsage, 0, 100), 1),
                    WorkingSetBytes = _process.WorkingSet64
                };
                _lastCpuTime = cpuTime;
                _lastProcessSample = now;
            }

            if (_lastStorageSample == 0 ||
                Stopwatch.GetElapsedTime(_lastStorageSample, now) >= TimeSpan.FromSeconds(5))
            {
                _snapshot = _snapshot with
                {
                    TotalSpaceBytes = _drive.TotalSize,
                    FreeSpaceBytes = _drive.AvailableFreeSpace
                };
                _lastStorageSample = now;
            }

            return _snapshot;
        }
    }

    public void Dispose() => _process.Dispose();

    internal readonly record struct ResourceSnapshot(
        double CpuUsage, long WorkingSetBytes, long TotalSpaceBytes, long FreeSpaceBytes);
}
