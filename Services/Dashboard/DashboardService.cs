using NebulaServer.Models.Dashboard;
using NebulaServer.Services.Devices;
using NebulaServer.Services.Server;

namespace NebulaServer.Services.Dashboard;

public sealed class DashboardService : IDisposable
{
    private readonly ServerStateService _serverState;
    private readonly JobQueueManager _jobs;
    private readonly StorageService _storage;
    private readonly DashboardResourceMonitor _resources;
    private readonly DeviceRegistryService _devices;

    private readonly DateTime _startedAt = DateTime.UtcNow;

    public DashboardService(
     ServerStateService serverState,
     JobQueueManager jobs,
     StorageService storage,
     DeviceRegistryService devices)
    {
        _serverState = serverState;
        _jobs = jobs;
        _storage = storage;
        _devices = devices;

        _resources = new DashboardResourceMonitor(storage.DownloadsPath);
    }

    public void Dispose() => _resources.Dispose();

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
        response.Runtime.NgrokRunning = false;
        response.Runtime.PythonVersion = state.PythonVersion;
        response.Runtime.FfmpegReady = File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));

        //---------------------------------------
        // Network
        //---------------------------------------
        response.Network.DeviceId = state.DeviceId;
        response.Network.LocalIp = state.LocalIp;
        response.Network.Port = state.Port;
        response.Network.LocalUrl = $"http://{state.LocalIp}:{state.Port}";
        response.Network.PublicUrl = "";
        response.Network.RemoteAvailable = false;

        //---------------------------------------
        // Storage
        //---------------------------------------
        response.Storage.DownloadsFolder = _storage.DownloadsPath;

        var resources = _resources.GetSnapshot();
        response.Storage.TotalSpaceBytes = resources.TotalSpaceBytes;
        response.Storage.FreeSpaceBytes = resources.FreeSpaceBytes;
        response.Performance.CpuUsage = resources.CpuUsage;
        response.Performance.WorkingSetBytes = resources.WorkingSetBytes;
        response.Performance.MemoryUsageMb = Math.Round(resources.WorkingSetBytes / 1024d / 1024d, 1);

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
        // Devices (??????? ???????)
        //---------------------------------------
        // 1. ????? ???? ??????? ????? ?? ?? ??? ???? ???? ??????
        _devices.MarkOffline(TimeSpan.FromSeconds(30));

        // 2. ????? ????? ??????? ?? ????????? (???? ?? ???? ????? Devices ???? DashboardResponse)
        response.Devices = _devices.GetAll().ToList();

        return response;
    }
}

