using NebulaServer.Models.Dashboard;
using NebulaServer.Services.Devices;
using NebulaServer.Services.Ngrok;
// تم إزالة IPairingService كما ذكرت
using System.Diagnostics;

namespace NebulaServer.Services.Dashboard;

public class DashboardService
{
    private readonly ServerStateService _serverState;
    private readonly JobQueueManager _jobs;
    private readonly StorageService _storage;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DeviceRegistryService _devices;
    private readonly PublicUrlProvider _publicUrlProvider;

    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuCheck;
    private double _lastCalculatedCpu;
    private readonly object _cpuLock = new object();

    private readonly DateTime _startedAt = DateTime.UtcNow;

    public DashboardService(
     ServerStateService serverState,
     JobQueueManager jobs,
     StorageService storage,
     DeviceRegistryService devices,
     PublicUrlProvider publicUrlProvider)
    {
        _serverState = serverState;
        _jobs = jobs;
        _storage = storage;
        _devices = devices;
        _publicUrlProvider = publicUrlProvider;

        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuCheck = DateTime.UtcNow;
        _lastCalculatedCpu = 0;

        // تم إزالة الأسطر الخاطئة من هنا
    }

    private double GetCpuUsage()
    {
        lock (_cpuLock)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (now - _lastCpuCheck).TotalMilliseconds;

            if (elapsedMs > 500)
            {
                var cpu = _process.TotalProcessorTime;
                var cpuUsedMs = (cpu - _lastCpuTime).TotalMilliseconds;

                _lastCalculatedCpu = Math.Round(
                    cpuUsedMs / (Environment.ProcessorCount * elapsedMs) * 100,
                    1);

                _lastCpuTime = cpu;
                _lastCpuCheck = now;
            }

            return _lastCalculatedCpu;
        }
    }

    private void FillPerformance(DashboardResponse response)
    {
        _process.Refresh();

        response.Performance.CpuUsage = GetCpuUsage();
        response.Performance.WorkingSetBytes = _process.WorkingSet64;
        response.Performance.MemoryUsageMb =
            Math.Round(_process.WorkingSet64 / 1024d / 1024d, 1);
    }

    public DashboardResponse GetDashboard()
    {
        var state = _serverState.Get();
        var response = new DashboardResponse();

        //---------------------------------------
        // Server
        //---------------------------------------
        response.Server.Name = "Nebula Server";
        response.Server.Version = state.Version;
        response.Server.IsRunning = true;
        response.Server.StartedAt = _startedAt;
        response.Server.Uptime = DateTime.UtcNow - _startedAt;

        //---------------------------------------
        // Runtime
        //---------------------------------------
        response.Runtime.PythonReady = state.IsPythonReady;
        response.Runtime.NgrokRunning = state.IsNgrokRunning;
        response.Runtime.PythonVersion = state.PythonVersion;
        response.Runtime.FfmpegReady = File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));

        //---------------------------------------
        // Network
        //---------------------------------------
        response.Network.DeviceId = state.DeviceId;
        response.Network.LocalIp = state.LocalIp;
        response.Network.Port = state.Port;
        response.Network.LocalUrl = $"http://{state.LocalIp}:{state.Port}";
        response.Network.PublicUrl = _publicUrlProvider.Get() ?? state.PublicUrl ?? "";
        response.Network.RemoteAvailable = state.IsNgrokRunning;

        //---------------------------------------
        // Storage
        //---------------------------------------
        response.Storage.DownloadsFolder = _storage.DownloadsPath;

        var drive = new DriveInfo(Path.GetPathRoot(_storage.DownloadsPath)!);
        response.Storage.TotalSpaceBytes = drive.TotalSize;
        response.Storage.FreeSpaceBytes = drive.AvailableFreeSpace;

        FillPerformance(response);

        //---------------------------------------
        // Queue
        //---------------------------------------
        var stats = _jobs.GetStatistics();
        response.Queue.QueuedJobs = stats.QueuedJobs;
        response.Queue.RunningJobs = stats.RunningJobs;
        response.Queue.CompletedJobs = stats.CompletedJobs;
        response.Queue.FailedJobs = stats.FailedJobs;

        //---------------------------------------
        // Current Job
        //---------------------------------------
        var current = _jobs.GetCurrentJob();

        if (current != null)
        {
            response.CurrentJob.HasJob = true;
            response.CurrentJob.JobId = current.JobId;
            response.CurrentJob.Title =
                string.IsNullOrWhiteSpace(current.OriginalTitle)
                    ? current.OutputFileName ?? ""
                    : current.OriginalTitle;

            response.CurrentJob.State = current.State.ToString();
            response.CurrentJob.Progress = current.ProgressPercentage;
            response.CurrentJob.Speed = current.Speed;
            response.CurrentJob.Eta = current.Eta;
            response.CurrentJob.ThumbnailUrl = current.ThumbnailUrl;
        }

        //---------------------------------------
        // Devices (الإضافة الجديدة)
        //---------------------------------------
        // 1. تحديث حالة الأجهزة أولاً في كل مرة نطلب فيها اللوحة
        _devices.MarkOffline(TimeSpan.FromSeconds(30));

        // 2. تعبئة قائمة الأجهزة في الاستجابة (تأكد من وجود خاصية Devices داخل DashboardResponse)
        response.Devices = _devices.GetAll().ToList();

        return response;
    }
}
