using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;
using NebulaServer.Services.Preview;

namespace NebulaServer.Services;

/// <summary>
/// Manages local storage paths, file locks, playlist archives, and artifact retention.
/// </summary>
public class StorageService
{
    private readonly ILogger<StorageService> _logger;
    private readonly MediaPreviewService _previewService;
    private readonly SocialMediaMetadataExtractor _socialMediaMetadata;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingPlaylistDeletion = new();

    public static readonly TimeSpan DefaultPlaylistRetention = TimeSpan.FromSeconds(20);

    public string DownloadsPath { get; }
    public string JobsPath { get; }
    public string TempPath { get; }
    public string LogsPath { get; }
    public string ArchivesPath { get; }

    public StorageService(
        ILogger<StorageService> logger,
        MediaPreviewService? previewService = null,
        SocialMediaMetadataExtractor? socialMediaMetadata = null)
    {
        _logger = logger;
        _previewService = previewService ?? new MediaPreviewService(logger);
        _socialMediaMetadata = socialMediaMetadata ?? new SocialMediaMetadataExtractor(logger);

        DownloadsPath = RuntimePaths.Downloads;
        JobsPath = RuntimePaths.Jobs;
        TempPath = RuntimePaths.Temp;
        LogsPath = RuntimePaths.Logs;
        ArchivesPath = RuntimePaths.Archives;

        RuntimePaths.EnsureDirectories();
    }

    // =========================================================
    // Delegated URL & Preview Helpers (backwards-compatible facade)
    // =========================================================

    public static string NormalizeInputUrl(string input) => UrlNormalizer.NormalizeInputUrl(input);

    public Task<string?> ExtractSocialMediaImageUrlAsync(string url, CancellationToken cancellationToken = default)
        => _socialMediaMetadata.ExtractImageUrlAsync(url, cancellationToken);

    public Task<string?> ExtractThumbnailQuicklyAsync(string url, string? proxy = null)
        => _previewService.ExtractThumbnailQuicklyAsync(url, proxy);

    public Task<PreviewData> ExtractPreviewDataAsync(string url, string? proxy = null)
        => _previewService.ExtractPreviewDataAsync(url, proxy);

    // =========================================================
    // Core Storage, Locking & Playlist Archives
    // =========================================================

    public string GetJobJsonPath(string jobId) => Path.Combine(JobsPath, $"{jobId}.json");
    public string GetDownloadFilePath(string fileName) => Path.Combine(DownloadsPath, fileName);
    public string GetPlaylistDirectoryPath(string jobId) => Path.Combine(DownloadsPath, jobId);

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
        if (!Directory.Exists(sourceDirectory)) return Array.Empty<string>();

        return Directory.EnumerateFiles(sourceDirectory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CreatePlaylistArchive(string jobId, string? archiveFileName)
    {
        var sourceDirectory = GetPlaylistDirectoryPath(jobId);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Playlist output directory was not found for job {jobId}.");

        var safeFileName = string.IsNullOrWhiteSpace(archiveFileName) ? $"{jobId}.zip" : archiveFileName;
        if (!safeFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) safeFileName += ".zip";

        var archivePath = Path.Combine(ArchivesPath, safeFileName);
        if (File.Exists(archivePath)) File.Delete(archivePath);

        ZipFile.CreateFromDirectory(sourceDirectory, archivePath, CompressionLevel.Fastest, false);
        _logger.LogInformation("Created playlist archive for job {JobId} at {ArchivePath}", jobId, archivePath);

        return archivePath;
    }

    public void SchedulePlaylistDeletion(string jobId, TimeSpan? delay = null)
    {
        var effectiveDelay = delay ?? DefaultPlaylistRetention;
        var dueAt = DateTime.UtcNow.Add(effectiveDelay);
        _pendingPlaylistDeletion[jobId] = dueAt;
        _logger.LogInformation("Scheduled fallback playlist cleanup for job {JobId} at {DueAt:u} (in {Delay})", jobId, dueAt, effectiveDelay);
    }

    public void DeletePlaylistAfterTransfer(string jobId)
    {
        var path = GetPlaylistDirectoryPath(jobId);
        var titleFilePath = Path.Combine(DownloadsPath, $"{jobId}_title.txt");
        var archiveFilePath = Path.Combine(ArchivesPath, $"{jobId}.zip");

        try
        {
            if (Directory.Exists(path))
            {
                if (DirectoryContainsLockedFiles(path))
                {
                    _logger.LogWarning("Playlist folder for job {JobId} still has locked files right after transfer; leaving it for the scheduled fallback cleanup at {Path}", jobId, path);
                    return;
                }
                Directory.Delete(path, true);
                _logger.LogInformation("Deleted playlist folder for job {JobId} at {Path}", jobId, path);
            }

            if (File.Exists(titleFilePath))
            {
                try { File.Delete(titleFilePath); } catch { _logger.LogWarning("Could not delete title file {File}", titleFilePath); }
            }

            if (File.Exists(archiveFilePath))
            {
                try { File.Delete(archiveFilePath); } catch { _logger.LogWarning("Could not delete archive file {File}", archiveFilePath); }
            }

            _pendingPlaylistDeletion.TryRemove(jobId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete artifacts for job {JobId} after transfer; will rely on scheduled fallback cleanup", jobId);
        }
    }

    public void ProcessScheduledPlaylistDeletion()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _pendingPlaylistDeletion.ToArray())
        {
            if (item.Value > now) continue;

            var jobId = item.Key;
            var path = GetPlaylistDirectoryPath(jobId);

            try
            {
                if (Directory.Exists(path))
                {
                    if (DirectoryContainsLockedFiles(path))
                    {
                        _logger.LogWarning("Scheduled cleanup skipped job {JobId}: files still locked at {Path}; will retry next cycle", jobId, path);
                        continue;
                    }
                    Directory.Delete(path, true);
                    _logger.LogInformation("Scheduled fallback cleanup deleted playlist folder for job {JobId} at {Path}", jobId, path);
                }
                _pendingPlaylistDeletion.TryRemove(jobId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled cleanup failed for job {JobId} at {Path}; will retry next cycle", jobId, path);
            }
        }
    }

    // =========================================================
    // Artifact Retention & Cleanup
    // =========================================================

    public void CleanupExpiredArtifacts(TimeSpan maxAge, ISet<string> protectedJobIds, bool keepDownloads = false)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        CleanupFiles(JobsPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupFiles(ArchivesPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupFiles(TempPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupDirectories(TempPath, cutoff, protectedJobIds);

        // Desktop downloads are a user's library, not temporary transfer artifacts.
        if (keepDownloads) return;

        CleanupFiles(DownloadsPath, cutoff, protectedJobIds, filePath =>
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (name.EndsWith("_title", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 6);

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
        if (!Directory.Exists(directoryPath)) return;
        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
        {
            if (protectedJobIds.Contains(jobIdSelector(filePath))) continue;
            var info = new FileInfo(filePath);
            if (info.LastWriteTimeUtc >= cutoff || IsLocked(filePath)) continue;
            try { info.Delete(); } catch { }
        }
    }

    private void CleanupDirectories(string directoryPath, DateTime cutoff, ISet<string> protectedJobIds)
    {
        if (!Directory.Exists(directoryPath)) return;
        foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
        {
            var directoryName = Path.GetFileName(childDirectory);
            if (protectedJobIds.Contains(directoryName)) continue;
            var info = new DirectoryInfo(childDirectory);
            if (info.LastWriteTimeUtc >= cutoff || DirectoryContainsLockedFiles(childDirectory)) continue;
            try { info.Delete(recursive: true); } catch { }
        }
    }

    private bool DirectoryContainsLockedFiles(string directoryPath)
    {
        try { return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).Any(IsLocked); }
        catch { return true; }
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
        internal FileLockLease(string path, SemaphoreSlim gate) { Path = path; _gate = gate; }
        public string Path { get; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Release();
        }
    }
}
