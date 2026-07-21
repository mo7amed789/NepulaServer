using NebulaServer.Models;
using NebulaServer.Models.Dashboard;
using NebulaServer.Models.Jobs;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using NebulaServer.Services.Jobs;

namespace NebulaServer.Services;

public interface IDashboardEvents
{
    Task JobProgress(DownloadJob job);
}

public class JobQueueManager : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ArtifactRetention = TimeSpan.FromHours(24);

    private readonly ILogger<JobQueueManager> _logger;
    private readonly PythonProcessService _python;
    private readonly StorageService _storage;
    private readonly IDashboardEvents _dashboardEvents;
    private readonly JobStore _jobStore;
    private readonly PlaylistEventBus _playlistEventBus;

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly Queue<string> _queue = new();

    // تتبع الـ Tokens لكل مهمة للتمكن من إيقافها
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public JobQueueManager(
        ILogger<JobQueueManager> logger,
        PythonProcessService python,
        StorageService storage,
        IDashboardEvents dashboardEvents,
        JobStore jobStore,
        PlaylistEventBus playlistEventBus)
    {
        _logger = logger;
        _python = python;
        _storage = storage;
        _dashboardEvents = dashboardEvents;
        _jobStore = jobStore;
        _playlistEventBus = playlistEventBus;
    }

    // =========================================================================
    // Public API
    // =========================================================================

    public DownloadJob CreateJob(JobRequest request)
    {
        var job = new DownloadJob
        {
            Request = request,
            Message = request.IsPlaylist ? "Playlist job queued" : "Job queued",
            CreatedAt = DateTime.UtcNow // تأكد من وجود هذه الخاصية في كائن DownloadJob لديك
        };

        _jobs[job.JobId] = job;
        lock (_queue) { _queue.Enqueue(job.JobId); }
        PersistJob(job);

        _logger.LogInformation(
            "Queued job {JobId}. Playlist: {IsPlaylist}, Format: {Format}, ExtractVocals: {ExtractVocals}",
            job.JobId, request.IsPlaylist, request.Format, request.ExtractVocals);

        _ = Task.Run(() => _dashboardEvents.JobProgress(job));

        return job;
    }

    public DownloadJob? GetJob(string id)
    {
        _jobs.TryGetValue(id, out var job);
        return job;
    }

    public void RemoveJob(string id)
    {
        if (_jobs.TryRemove(id, out var job))
        {
            _logger.LogInformation("Job {JobId} removed from queue memory.", id);
        }
    }

    public void CancelAllJobs()
    {
        foreach (var token in _activeJobTokens.Values)
        {
            try
            {
                token.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel a running job token during shutdown.");
            }
        }

        lock (_queue)
        {
            _queue.Clear();
        }
    }

    public void RestoreJob(DownloadJob job)
    {
        job.State = JobState.QUEUED;
        job.ProgressPercentage = "0%";
        job.Speed = string.Empty;
        job.Eta = string.Empty;
        job.Message ??= "Recovered after restart";

        _jobs[job.JobId] = job;
        lock (_queue) { _queue.Enqueue(job.JobId); }
        PersistJob(job);

        _ = Task.Run(() => _dashboardEvents.JobProgress(job));
    }

    public void RestoreTransferState(DownloadJob job, TransferSnapshot snapshot)
    {
        job.ManifestVersion = snapshot.ManifestVersion;
        job.EventSequence = snapshot.EventSequence;

        foreach (var item in snapshot.Items)
        {
            job.CompletedItems[item.Index] = new PlaylistItemResult
            {
                ItemId = item.ItemId,
                Index = item.Index,
                FileName = item.FileName,
                Title = item.Title,
                State = item.State,
                SizeBytes = item.SizeBytes,
                Sha256 = item.Sha256,
                CreatedUtc = item.CreatedUtc,
                CompletedAt = item.CompletedAt,
                Sequence = item.Sequence
            };
        }

        foreach (var evt in snapshot.Events.OrderBy(e => e.Sequence))
        {
            job.Events[evt.Sequence] = evt;
        }

        PersistJob(job);

    }

    public bool TrySetPlaylistItemState(DownloadJob job, int index, TransferState state)
    {
        if (!job.CompletedItems.TryGetValue(index, out var item))
            return false;

        item.State = state;
        BumpManifestVersion(job);
        if (state == TransferState.Completed)
        {
            AppendTransferEvent(job, TransferEventType.ItemCompleted, item, $"Item {index} transfer completed.");
        }
        PersistJob(job);
        return true;
    }

    public IReadOnlyList<TransferEventRecord> GetTransferEventsAfter(string jobId, long afterSequence)
    {
        var job = GetJob(jobId);
        if (job is null)
            return Array.Empty<TransferEventRecord>();

        return job.Events.Values
            .Where(evt => evt.Sequence > afterSequence)
            .OrderBy(evt => evt.Sequence)
            .ToArray();
    }

    // دالة الإلغاء الرئيسية
    public async Task<bool> CancelJobAsync(string jobId)
    {
        _logger.LogWarning("Cancel requested for job {JobId}", jobId);
        if (!_jobs.TryGetValue(jobId, out var job)) return false;

        // إذا كانت المهمة قيد التشغيل حالياً، نرسل أمر الإلغاء للـ Token
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogWarning("Cancelling token for job {JobId}", jobId);
            cts.Cancel();
            return true;
        }

        // إذا كانت المهمة في الطابور ولم تبدأ بعد
        if (job.State == JobState.QUEUED)
        {
            job.State = JobState.CANCELED;
            job.Message = "Canceled before starting";
            PersistJob(job);
            await _dashboardEvents.JobProgress(job);

            _jobs.TryRemove(jobId, out _);
            return true;
        }

        return false;
    }

    // =========================================================================
    // Background loop
    // =========================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RunScheduledCleanup();

                string? nextJob = null;

                lock (_queue)
                {
                    if (_queue.Count > 0)
                        nextJob = _queue.Dequeue();
                }

                if (nextJob is null)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await ProcessJob(nextJob, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("JobQueueManager stopped.");
        }
    }

    // =========================================================================
    // Job execution
    // =========================================================================

    private async Task ProcessJob(string jobId, CancellationToken globalToken)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

        // إنشاء Token خاص بهذه المهمة وربطه بالـ Token العام للخادم
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        _activeJobTokens[jobId] = jobCts;
        var token = jobCts.Token;

        try
        {
            job.State = JobState.DOWNLOADING;
            job.Message = "Preparing job metadata";
            PersistJob(job);
            await _dashboardEvents.JobProgress(job);

            var jsonPath = _storage.GetJobJsonPath(job.JobId);

            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(new { JobId = job.JobId, Request = job.Request }),
                token);

            using var process = _python.StartProcess(jsonPath);

            // تسجيل أمر لقتل عملية البايثون فور استدعاء cts.Cancel()
            using var processKiller = token.Register(() =>
            {
                _logger.LogWarning("Killing python process for {JobId}", jobId);

                try
                {
                    if (!process.HasExited)
                        process.Kill(true); // true لقتل كل العمليات الفرعية (Child processes) أيضاً
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kill failed for {JobId}", jobId);
                }
            });

            var errorBuffer = new StringBuilder();

            var outputTask = ReadStandardOutputAsync(process, job, token);
            var errorTask = ReadStandardErrorAsync(process, job, errorBuffer, token);

            await process.WaitForExitAsync(token);
            await Task.WhenAll(outputTask, errorTask);

            // التحقق من الفشل أو الإكمال فقط إذا لم يتم الإلغاء
            if (!token.IsCancellationRequested)
            {
                if (process.ExitCode != 0 && job.State != JobState.FAILED)
                {
                    job.State = JobState.FAILED;
                    job.Message = BuildFailureMessage(
                        errorBuffer.ToString(),
                        $"Python engine exited with code {process.ExitCode}.");
                    AppendTransferEvent(job, TransferEventType.JobFailed, null, job.Message);
                    PersistJob(job);

                    await _dashboardEvents.JobProgress(job);
                }

                if (job.State != JobState.COMPLETED && job.State != JobState.FAILED)
                {
                    job.State = JobState.FAILED;
                    job.Message ??= "The Python engine finished without a final result.";
                    AppendTransferEvent(job, TransferEventType.JobFailed, null, job.Message);
                    PersistJob(job);
                    await _dashboardEvents.JobProgress(job);
                }
            }

            if (!token.IsCancellationRequested)
            {
                if (job.State is JobState.COMPLETED or JobState.FAILED or JobState.CANCELED)
                {
                    _playlistEventBus.Complete(job.JobId);
                }

                _logger.LogInformation(
                    "Job {JobId} finished with state {State}. Message: {Message}",
                    job.JobId, job.State, job.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // هذا الجزء سيعمل فوراً عند الضغط على Cancel
            job.State = JobState.CANCELED;
            job.Message = "Job was canceled by user.";
            AppendTransferEvent(job, TransferEventType.JobCanceled, null, job.Message);
            PersistJob(job);
            await _dashboardEvents.JobProgress(job);
            _playlistEventBus.Complete(job.JobId);
            _logger.LogInformation("Job {JobId} was canceled gracefully.", job.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", job.JobId);
            job.State = JobState.FAILED;
            job.Message = ex.Message;
            AppendTransferEvent(job, TransferEventType.JobFailed, null, job.Message);
            PersistJob(job);
            await _dashboardEvents.JobProgress(job);
            _playlistEventBus.Complete(job.JobId);
        }
        finally
        {
            // تنظيف الـ Token من القاموس بعد انتهاء المهمة أو إلغائها
            _activeJobTokens.TryRemove(jobId, out _);
        }
    }

    // =========================================================================
    // stdout / stderr readers
    // =========================================================================

    private async Task ReadStandardOutputAsync(
        System.Diagnostics.Process process,
        DownloadJob job,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            _logger.LogInformation("Python stdout [{JobId}]: {Line}", job.JobId, line);
            await ParseEngineMessageAsync(job, line);
        }
    }

    private async Task ReadStandardErrorAsync(
        System.Diagnostics.Process process,
        DownloadJob job,
        StringBuilder buffer,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(token);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            buffer.AppendLine(line);
            _logger.LogWarning("Python stderr [{JobId}]: {Line}", job.JobId, line);
        }
    }

    // =========================================================================
    // Protocol parsing
    // =========================================================================

    private async Task ParseEngineMessageAsync(DownloadJob job, string line)
    {
        if (TryExtractProtocolLine(line, "JOB_PROGRESS|", out var progressLine))
        {
            var parts = progressLine.Split('|', 5);
            if (parts.Length >= 5)
            {
                job.ProgressPercentage = parts[2];
                job.Speed = parts[3];
                job.Eta = parts[4];
                await _dashboardEvents.JobProgress(job);
            }
            return;
        }

        if (TryExtractProtocolLine(line, "JOB_STAGE|", out var stageLine))
        {
            var parts = stageLine.Split('|', 4);
            var stage = parts.Length >= 3 ? parts[2] : "PROCESSING";
            var details = parts.Length >= 4 ? parts[3] : null;

            // Per-item playlist completion — this is emitted once per file as
            // soon as it finishes processing, independent of overall job
            // progress. Don't let it clobber job.State/Message, which track
            // the *overall* job stage (DOWNLOADING/PROCESSING/etc).
            if (string.Equals(stage, "ITEM_COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                if (job.Request.IsPlaylist && !string.IsNullOrWhiteSpace(details))
                {
                    job.Message = $"Item ready: {details}";
                    PersistJob(job);
                    await _dashboardEvents.JobProgress(job);
                }
                return;
            }

            job.State = MapStageToState(stage);
            job.Message = string.IsNullOrWhiteSpace(details)
                ? BuildDefaultStageMessage(stage, job.Request.IsPlaylist)
                : details;
            PersistJob(job);

            await _dashboardEvents.JobProgress(job);
            return;
        }

        if (TryExtractProtocolLine(line, "JOB_RESULT|", out var resultLine))
        {
            var parts = resultLine.Split('|', 4);
            if (parts.Length < 3) return;

            var outputName = parts[2];
            var originalTitle = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3])
                ? parts[3]
                : null;

            if (job.Request.IsPlaylist)
            {
                var isFinalSummary = string.Equals(outputName, job.JobId, StringComparison.OrdinalIgnoreCase);

                if (isFinalSummary)
                {
                    job.Message = "Playlist ready for download";
                    AppendTransferEvent(job, TransferEventType.JobCompleted, null, job.Message);
                    _playlistEventBus.Complete(job.JobId);
                }
                else
                {
                    // outputName looks like "{jobId}/02_Title.mp4"
                    var fileName = Path.GetFileName(outputName);
                    var indexPrefix = fileName.Split('_', 2)[0];

                    if (int.TryParse(indexPrefix, out var idx) &&
                        !job.CompletedItems.ContainsKey(idx))
                    {
                        var item = BuildPlaylistItem(job, fileName, idx, originalTitle);

                        job.CompletedItems[idx] = item;
                        BumpManifestVersion(job);
                        AppendTransferEvent(job, TransferEventType.ItemReady, item, $"Item ready: {fileName}");
                        _playlistEventBus.Publish(job.JobId, item);
                    }

                    job.Message = $"Playlist item ready: {fileName}";
                }
            }
            else
            {
                job.OutputFileName = outputName;
                if (originalTitle != null) job.OriginalTitle = originalTitle;
                job.ProgressPercentage = "100%";
                job.Speed = string.Empty;
                job.Eta = string.Empty;
                job.State = JobState.COMPLETED;
                job.Message = "File ready for download";
                AppendTransferEvent(job, TransferEventType.JobCompleted, null, job.Message);
            }

            PersistJob(job);
            await _dashboardEvents.JobProgress(job);
            return;
        }

        if (TryExtractProtocolLine(line, "JOB_THUMBNAIL|", out var thumbnailLine))
        {
            var parts = thumbnailLine.Split('|', 3);
            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
            {
                job.ThumbnailUrl = parts[2];
                await _dashboardEvents.JobProgress(job);
            }
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static bool TryExtractProtocolLine(string line, string prefix, out string extractedLine)
    {
        var index = line.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0) { extractedLine = string.Empty; return false; }
        extractedLine = line[index..];
        return true;
    }

    private static JobState MapStageToState(string stage) =>
        stage.ToUpperInvariant() switch
        {
            "DOWNLOADING" => JobState.DOWNLOADING,
            "PROCESSING" => JobState.PROCESSING,
            "SEGMENTING" => JobState.SEGMENTING,
            "SEPARATING_VOCALS" => JobState.SEPARATING_VOCALS,
            "MERGING_AUDIO" => JobState.MERGING_AUDIO,
            "COMPLETED" => JobState.COMPLETED,
            "FAILED" => JobState.FAILED,
            _ => JobState.PROCESSING
        };

    private static string BuildDefaultStageMessage(string stage, bool isPlaylist) =>
        stage.ToUpperInvariant() switch
        {
            "DOWNLOADING" => isPlaylist ? "Downloading playlist items" : "Downloading media",
            "PROCESSING" => "Processing media",
            "SEGMENTING" => "Applying requested segment",
            "SEPARATING_VOCALS" => "Separating vocals",
            "MERGING_AUDIO" => "Merging separated audio",
            "FAILED" => "Job failed",
            _ => "Processing media"
        };

    private static string BuildFailureMessage(string stderr, string fallback)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return fallback;

        var lines = stderr.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length == 0 ? fallback : lines[^1];
    }

    private void PersistJob(DownloadJob job)
    {
        try
        {
            _jobStore.Save(new JobSnapshot
            {
                Id = job.JobId,
                Url = job.Request.Url,
                Status = job.State.ToString(),
                Progress = ParseProgress(job.ProgressPercentage),
                OutputPath = job.OutputFileName,
                CreatedAt = job.CreatedAt,
                RequestJson = JsonSerializer.Serialize(job.Request),
                TransferJson = JsonSerializer.Serialize(BuildTransferSnapshot(job))
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist job snapshot for {JobId}.", job.JobId);
        }
    }

    private static TransferSnapshot BuildTransferSnapshot(DownloadJob job)
    {
        return new TransferSnapshot
        {
            JobId = job.JobId,
            ManifestVersion = job.ManifestVersion,
            EventSequence = job.EventSequence,
            UpdatedUtc = DateTime.UtcNow,
            Items = job.CompletedItems.Values
                .OrderBy(item => item.Index)
                .Select(item => new TransferSnapshotItem
                {
                    ItemId = item.ItemId,
                    Index = item.Index,
                    FileName = item.FileName,
                    Title = item.Title,
                    State = item.State,
                    SizeBytes = item.SizeBytes,
                    Sha256 = item.Sha256,
                    CreatedUtc = item.CreatedUtc,
                    CompletedAt = item.CompletedAt,
                    Sequence = item.Sequence
                })
                .ToList(),
            Events = job.Events.Values
                .OrderBy(evt => evt.Sequence)
                .ToList()
        };
    }

    private PlaylistItemResult BuildPlaylistItem(
        DownloadJob job,
        string fileName,
        int index,
        string? originalTitle)
    {
        var filePath = Path.Combine(_storage.GetPlaylistDirectoryPath(job.JobId), fileName);
        var fileInfo = new FileInfo(filePath);

        return new PlaylistItemResult
        {
            ItemId = ComputeStableItemId(filePath, job.JobId, index),
            Index = index,
            FileName = fileName,
            Title = originalTitle ?? fileName,
            State = TransferState.Ready,
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            Sha256 = fileInfo.Exists ? ComputeSha256(filePath) : string.Empty,
            CreatedUtc = fileInfo.Exists ? fileInfo.CreationTimeUtc : DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            Sequence = NextEventSequence(job)
        };
    }

    private static string ComputeStableItemId(string filePath, string jobId, int index)
    {
        if (File.Exists(filePath))
        {
            var contentHash = ComputeSha256(filePath);
            var itemBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{contentHash}:{index}"));
            return Convert.ToHexString(itemBytes).ToLowerInvariant();
        }

        var hashSource = $"{jobId}:{index}".ToLowerInvariant();
        var fallbackBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashSource));
        return Convert.ToHexString(fallbackBytes).ToLowerInvariant();
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private long NextEventSequence(DownloadJob job)
    {
        lock (job.PlaylistLock)
        {
            job.EventSequence++;
            return job.EventSequence;
        }
    }

    private void BumpManifestVersion(DownloadJob job)
    {
        lock (job.PlaylistLock)
        {
            job.ManifestVersion++;
        }
    }

    private TransferEventRecord AppendTransferEvent(
        DownloadJob job,
        TransferEventType type,
        PlaylistItemResult? item,
        string? message = null)
    {
        lock (job.PlaylistLock)
        {
            job.EventSequence++;

            var evt = new TransferEventRecord
            {
                Sequence = job.EventSequence,
                Type = type,
                ItemId = item?.ItemId,
                Index = item?.Index,
                ManifestVersion = job.ManifestVersion,
                CreatedUtc = DateTime.UtcNow,
                Message = message
            };

            job.Events[evt.Sequence] = evt;
            PersistJob(job);
            return evt;
        }
    }

    private static double ParseProgress(string progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
            return 0;

        var cleaned = progress.Trim().TrimEnd('%');
        return double.TryParse(cleaned, out var value) ? value : 0;
    }

    // =========================================================================
    // Cleanup & Statistics
    // =========================================================================

    private void RunScheduledCleanup()
    {
        var now = DateTime.UtcNow;

        if (now - _lastCleanupUtc < CleanupInterval)
            return;

        _lastCleanupUtc = now;

        try
        {
            var protectedIds = _jobs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            _storage.CleanupExpiredArtifacts(
                ArtifactRetention,
                protectedIds);

            _storage.ProcessScheduledPlaylistDeletion();

            TrimExpiredJobs(now.Subtract(ArtifactRetention));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup pass failed");
        }
    }

    public DownloadJob? GetCurrentJob()
    {
        return _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault(j =>
                j.State is
                    JobState.DOWNLOADING or
                    JobState.PROCESSING or
                    JobState.SEGMENTING or
                    JobState.SEPARATING_VOCALS or
                    JobState.MERGING_AUDIO);
    }

    public QueueStatistics GetStatistics()
    {
        return new QueueStatistics
        {
            QueuedJobs = _jobs.Values.Count(j =>
                j.State == JobState.QUEUED),

            RunningJobs = _jobs.Values.Count(j =>
                j.State is
                    JobState.DOWNLOADING or
                    JobState.PROCESSING or
                    JobState.SEGMENTING or
                    JobState.SEPARATING_VOCALS or
                    JobState.MERGING_AUDIO),

            CompletedJobs = _jobs.Values.Count(j =>
                j.State == JobState.COMPLETED),

            FailedJobs = _jobs.Values.Count(j =>
                j.State == JobState.FAILED),

            TotalJobs = _jobs.Count
        };
    }

    private void TrimExpiredJobs(DateTime cutoffUtc)
    {
        foreach (var item in _jobs)
        {
            if (item.Value.CreatedAt >= cutoffUtc) continue;

            if (item.Value.State is not (JobState.COMPLETED or JobState.FAILED or JobState.CANCELED))
                continue;

            _jobs.TryRemove(item.Key, out _);
        }
    }
}
