using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NebulaServer.Helpers;
using System.Collections.Concurrent;

namespace NebulaServer.Services;

public class PreviewData
{
    public string? ThumbnailUrl { get; set; }
    public List<string> AvailableQualities { get; set; } = new();
}

public class StorageService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ThumbnailCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingPlaylistDeletion = new();
    // 🌟 NEW: caches the combined preview result (thumbnail + available qualities)
    // keyed by URL, so repeated typing/edits of the same link don't re-spawn yt-dlp.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PreviewData> PreviewCache = new();
    public void SchedulePlaylistDeletion(string jobId, TimeSpan delay)
    {
        _pendingPlaylistDeletion[jobId] = DateTime.UtcNow.Add(delay);
    }
    public string DownloadsPath { get; }
    public string JobsPath { get; }
    public string TempPath { get; }
    public string LogsPath { get; }
    public string ArchivesPath { get; }
    public void ProcessScheduledPlaylistDeletion()
    {
        var now = DateTime.UtcNow;

        foreach (var item in _pendingPlaylistDeletion.ToArray())
        {
            if (item.Value > now)
                continue;

            try
            {
                var path = GetPlaylistDirectoryPath(item.Key);

                if (Directory.Exists(path))
                    Directory.Delete(path, true);

                _pendingPlaylistDeletion.TryRemove(item.Key, out _);
            }
            catch
            {
                // سيحاول مرة أخرى في دورة التنظيف القادمة
            }
        }
    }
    public StorageService()
    {
        DownloadsPath = RuntimePaths.Downloads;
        JobsPath = RuntimePaths.Jobs;
        TempPath = RuntimePaths.Temp;
        LogsPath = RuntimePaths.Logs;
        ArchivesPath = RuntimePaths.Archives;

        RuntimePaths.EnsureDirectories();
    }

    public string GetJobJsonPath(string jobId)
    {
        return Path.Combine(JobsPath, $"{jobId}.json");
    }

    public string GetDownloadFilePath(string fileName)
    {
        return Path.Combine(DownloadsPath, fileName);
    }

    public string GetPlaylistDirectoryPath(string jobId)
    {
        return Path.Combine(DownloadsPath, jobId);
    }

    public async Task<FileLockLease> AcquireFileLockAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        var gate = _fileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        return new FileLockLease(normalizedPath, gate);
    }

    public IReadOnlyList<string> GetPlaylistFiles(string jobId)
    {
        var sourceDirectory = GetPlaylistDirectoryPath(jobId);

        if (!Directory.Exists(sourceDirectory))
            return Array.Empty<string>();

        return Directory
            .EnumerateFiles(sourceDirectory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CreatePlaylistArchive(string jobId, string? archiveFileName)
    {
        var sourceDirectory = GetPlaylistDirectoryPath(jobId);

        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Playlist output directory was not found for job {jobId}.");

        var safeFileName = string.IsNullOrWhiteSpace(archiveFileName) ? $"{jobId}.zip" : archiveFileName;

        if (!safeFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            safeFileName += ".zip";

        var archivePath = Path.Combine(ArchivesPath, safeFileName);

        if (File.Exists(archivePath))
            File.Delete(archivePath);

        ZipFile.CreateFromDirectory(
            sourceDirectory,
            archivePath,
            CompressionLevel.Fastest,
            includeBaseDirectory: false);

        return archivePath;
    }

    public async Task<string?> ExtractThumbnailQuicklyAsync(string url, string? proxy = null)
    {
        var trimmedUrl = url.Trim();
        var cacheKey = GetPreviewCacheKey(trimmedUrl, proxy);

        if (ThumbnailCache.TryGetValue(cacheKey, out var cachedThumbnail))
            return cachedThumbnail;

        try
        {
            var pythonExecutable = RuntimePaths.ResolvePythonExecutable();
            var bundledSitePackages = RuntimePaths.PythonSitePackages;

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RuntimePaths.Base
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("yt_dlp");
            startInfo.ArgumentList.Add("--no-check-certificates");
            startInfo.ArgumentList.Add("--no-config");
            startInfo.ArgumentList.Add("--flat-playlist");
            startInfo.ArgumentList.Add("--get-thumbnail");
            ApplyProxy(startInfo, proxy);
            startInfo.ArgumentList.Add(trimmedUrl);

            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";

            if (!string.IsNullOrWhiteSpace(bundledSitePackages) && Directory.Exists(bundledSitePackages))
            {
                var existingPythonPath = startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath)
                    ? pythonPath
                    : string.Empty;

                startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                    ? bundledSitePackages
                    : bundledSitePackages + Path.PathSeparator + existingPythonPath;
            }

            using var process = new Process { StartInfo = startInfo };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

            if (process.ExitCode != 0)
                return null;

            var output = await outputTask;
            var thumbnailUrl = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                     .LastOrDefault()?.Trim();

            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                ThumbnailCache[cacheKey] = thumbnailUrl;
                return thumbnailUrl;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 🌟 NEW: combined preview extraction — gets the thumbnail AND the
    /// real list of available video heights (e.g. ["1080","720","480"])
    /// in a single yt-dlp invocation via -J (full JSON dump), instead of
    /// the lightweight --get-thumbnail-only call used previously.
    /// This is intentionally a separate method (not a change to
    /// ExtractThumbnailQuicklyAsync) so nothing else that already depends
    /// on the fast thumbnail-only path is affected.
    /// </summary>
    public async Task<PreviewData> ExtractPreviewDataAsync(string url, string? proxy = null)
    {
        var trimmedUrl = url.Trim();
        var cacheKey = GetPreviewCacheKey(trimmedUrl, proxy);

        if (PreviewCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = new PreviewData();

        try
        {
            var pythonExecutable = RuntimePaths.ResolvePythonExecutable();
            var bundledSitePackages = RuntimePaths.PythonSitePackages;

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RuntimePaths.Base
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("yt_dlp");
            startInfo.ArgumentList.Add("--no-check-certificates");
            startInfo.ArgumentList.Add("--no-config");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--skip-download");
            startInfo.ArgumentList.Add("-J");
            ApplyProxy(startInfo, proxy);
            startInfo.ArgumentList.Add(trimmedUrl);

            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";

            if (!string.IsNullOrWhiteSpace(bundledSitePackages) && Directory.Exists(bundledSitePackages))
            {
                var existingPythonPath = startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath)
                    ? pythonPath
                    : string.Empty;

                startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                    ? bundledSitePackages
                    : bundledSitePackages + Path.PathSeparator + existingPythonPath;
            }

            using var process = new Process { StartInfo = startInfo };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

            if (process.ExitCode != 0)
                return result;

            var output = await outputTask;

            // -J prints one JSON object (or one per line for flat playlists,
            // but we don't pass --flat-playlist here so a single video URL
            // yields exactly one JSON line).
            var jsonLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(jsonLine))
                return result;

            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("thumbnail", out var thumbEl) && thumbEl.ValueKind == JsonValueKind.String)
            {
                result.ThumbnailUrl = thumbEl.GetString();
            }

            // Descending, de-duplicated set of real video heights for this URL.
            var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));

            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                foreach (var fmt in formats.EnumerateArray())
                {
                    var isAudioOnly = fmt.TryGetProperty("vcodec", out var vcodecEl)
                        && vcodecEl.ValueKind == JsonValueKind.String
                        && vcodecEl.GetString() == "none";

                    if (isAudioOnly)
                        continue;

                    if (fmt.TryGetProperty("height", out var heightEl) && heightEl.ValueKind == JsonValueKind.Number)
                    {
                        heights.Add(heightEl.GetInt32());
                    }
                }
            }

            result.AvailableQualities = heights.Select(h => h.ToString()).ToList();

            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
                ThumbnailCache[cacheKey] = result.ThumbnailUrl;

            PreviewCache[cacheKey] = result;
            return result;
        }
        catch
        {
            return result;
        }
    }

    private static string GetPreviewCacheKey(string url, string? proxy)
    {
        var proxyKey = string.IsNullOrWhiteSpace(proxy) ? string.Empty : proxy.Trim();
        return $"{url}\n{proxyKey}";
    }

    private static void ApplyProxy(ProcessStartInfo startInfo, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
            return;

        startInfo.ArgumentList.Add("--proxy");
        startInfo.ArgumentList.Add(proxy.Trim());
    }

    public void CleanupExpiredArtifacts(TimeSpan maxAge, ISet<string> protectedJobIds)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);

        CleanupFiles(JobsPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupFiles(ArchivesPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupFiles(TempPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupDirectories(TempPath, cutoff, protectedJobIds);

        CleanupFiles(DownloadsPath, cutoff, protectedJobIds, filePath =>
        {
            var name = Path.GetFileNameWithoutExtension(filePath);

            if (name.EndsWith("_title", StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(0, name.Length - 6);
            }

            var dir = Path.GetDirectoryName(filePath) ?? DownloadsPath;
            foreach (var id in protectedJobIds)
            {
                if (File.Exists(Path.Combine(dir, $"{id}_title.txt")) || name.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            return name;
        });

        CleanupDirectories(DownloadsPath, cutoff, protectedJobIds);
    }

    private void CleanupFiles(string directoryPath, DateTime cutoff, ISet<string> protectedJobIds, Func<string, string> jobIdSelector)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
        {
            if (protectedJobIds.Contains(jobIdSelector(filePath)))
                continue;

            var info = new FileInfo(filePath);
            if (info.LastWriteTimeUtc >= cutoff)
                continue;

            if (IsLocked(filePath))
                continue;

            try
            {
                info.Delete();
            }
            catch
            {
                // If a transfer started between the lock check and delete,
                // keep the artifact and try again on the next cleanup pass.
            }
        }
    }

    private void CleanupDirectories(string directoryPath, DateTime cutoff, ISet<string> protectedJobIds)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
        {
            var directoryName = Path.GetFileName(childDirectory);
            if (protectedJobIds.Contains(directoryName))
                continue;

            var info = new DirectoryInfo(childDirectory);
            if (info.LastWriteTimeUtc >= cutoff)
                continue;

            if (DirectoryContainsLockedFiles(childDirectory))
                continue;

            try
            {
                info.Delete(recursive: true);
            }
            catch
            {
                // Active transfers or transient file handles can race cleanup.
            }
        }
    }

    private bool DirectoryContainsLockedFiles(string directoryPath)
    {
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                if (IsLocked(filePath))
                    return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private bool IsLocked(string filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        return _fileLocks.TryGetValue(normalizedPath, out var gate) && gate.CurrentCount == 0;
    }

    public sealed class FileLockLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _disposed;

        internal FileLockLease(string path, SemaphoreSlim gate)
        {
            Path = path;
            _gate = gate;
        }

        
        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _gate.Release();
        }

    }
}
