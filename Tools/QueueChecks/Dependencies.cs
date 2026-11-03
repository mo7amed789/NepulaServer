using System.Collections.Concurrent;
using System.Diagnostics;
using NebulaServer.Helpers;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;

// Only external dependencies are replaced. The queue, store, protocol parsing,
// hashing, and event bus tested by this executable are the production source files.
namespace NebulaServer.Helpers
{
    public static class RuntimePaths
    {
        public static string Data { get; set; } = null!;
    }
}

namespace NebulaServer.Services
{
    public sealed class StorageService
    {
        public static string NormalizeInputUrl(string url) => url.Trim();
        public string GetJobJsonPath(string id) => Path.Combine(RuntimePaths.Data, id + ".json");
        public string GetPlaylistDirectoryPath(string id)
        {
            var directory = Path.Combine(RuntimePaths.Data, id);
            Directory.CreateDirectory(directory);
            return directory;
        }
        public void CleanupExpiredArtifacts(TimeSpan retention, HashSet<string> protectedIds, bool keepDownloads = false) { }
        public void ProcessScheduledPlaylistDeletion() { }
    }

    public sealed class PythonProcessService
    {
        public readonly ConcurrentBag<int> StartedProcessIds = new();
        public Process StartProcess(string jsonPath)
        {
            // A real sleeping process exercises pipe cancellation and process-tree cleanup
            // without invoking Python, touching the network, or downloading any media.
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("Start-Sleep -Seconds 30");
            var process = Process.Start(info)!;
            StartedProcessIds.Add(process.Id);
            return process;
        }
    }

    public sealed class NoopEvents : IDashboardEvents
    {
        public Task JobProgress(DownloadJob job) => Task.CompletedTask;
        public Task DownloadProgressChanged(DownloadProgress progress) => Task.CompletedTask;
    }
}
