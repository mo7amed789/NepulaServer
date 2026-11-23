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
using System.Globalization;
using System.Security.Cryptography; 
using NebulaServer.Helpers;
using NebulaServer.Services.Jobs;
using System.Threading.Channels;

namespace NebulaServer.Services;

public interface IDashboardEvents
{
    Task JobProgress(DownloadJob job);
    Task DownloadProgressChanged(NebulaServer.Models.Jobs.DownloadProgress progress);
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
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    // تتبع الـ Tokens لكل مهمة للتمكن من إيقافها
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private readonly int _workerCount;
    private readonly bool _keepDownloads;
    private static readonly object ConsoleOutputLock = new();
    private readonly ConcurrentDictionary<string, double> _lastConsolePercent = new();

    public JobQueueManager(
        ILogger<JobQueueManager> logger,
        IConfiguration configuration,
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
        _keepDownloads = configuration.GetValue("Storage:KeepDownloads", false);

        // Media downloads are network-bound. Keep enough workers active to
        // use fast connections without allowing an unbounded queue to thrash
        // CPU, disk, or the upstream provider.
        var defaultWorkers = Environment.ProcessorCount >= 8 ? 4 : 3;
        _workerCount = Math.Clamp(
            configuration.GetValue("Performance:MaxConcurrentDownloads", defaultWorkers),
            1,
            8);
    }

    // =========================================================================
    // Public API
    // =========================================================================

    public DownloadJob CreateJob(JobRequest request, PreviewData? preview = null)
    {
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            request.Url = StorageService.NormalizeInputUrl(request.Url);
        }

        var job = new DownloadJob
        {
            Request = request,
            Message = request.IsPlaylist ? "Playlist job queued" : "Job queued",
            CreatedAt = DateTime.UtcNow,
            OriginalTitle = preview?.Title ?? string.Empty,
            ThumbnailUrl = preview?.ThumbnailUrl ?? string.Empty
        };

        _jobs[job.JobId] = job;
        PersistJob(job);
        _queue.Writer.TryWrite(job.JobId);

        _logger.LogInformation(
            "Queued job {JobId}. Playlist: {IsPlaylist}, Format: {Format}, ExtractVocals: {ExtractVocals}",
            job.JobId, request.IsPlaylist, request.Format, request.ExtractVocals);

        _ = NotifyJobProgressAsync(job);

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

        while (_queue.Reader.TryRead(out _)) { }
    }

    public void RestoreJob(DownloadJob job)
    {
        job.State = JobState.QUEUED;
        job.ProgressPercentage = "0%";
        job.Speed = string.Empty;
        job.Eta = string.Empty;
        job.Message ??= "Recovered after restart";

        _jobs[job.JobId] = job;
        PersistJob(job);
        _queue.Writer.TryWrite(job.JobId);

        _ = NotifyJobProgressAsync(job);
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

        // Serialize dequeue/start with cancellation so a queued job cannot begin
        // after the API reports it canceled.
        lock (job.PlaylistLock)
        {
            if (_activeJobTokens.TryGetValue(jobId, out var cts))
            {
                _logger.LogWarning("Cancelling token for job {JobId}", jobId);
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { return false; }
                return true;
            }

            if (job.State != JobState.QUEUED) return false;
            job.State = JobState.CANCELED;
            job.Message = "Canceled before starting";
        }
        PersistJob(job);
        await _dashboardEvents.JobProgress(job);
        _jobs.TryRemove(jobId, out _);
        return true;
    }

    // =========================================================================
    // Background loop
    // =========================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Track every worker so hosted-service shutdown awaits their process cleanup.
            // Each reader takes a job only when it has capacity; no per-job Task.Run or
            // separate semaphore can drift away from the number of queued items.
            var workers = Enumerable.Range(0, _workerCount)
                .Select(_ => RunWorkerAsync(stoppingToken));
            await Task.WhenAll(workers.Append(RunCleanupAsync(stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("JobQueueManager stopped.");
        }
    }

    private async Task RunWorkerAsync(CancellationToken token)
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await ProcessJob(jobId, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A notification failure must not silently remove one worker forever.
                _logger.LogError(ex, "Job worker failed for {JobId}.", jobId);
            }
        }
    }

    private async Task RunCleanupAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        do
        {
            RunScheduledCleanup();
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    private async Task NotifyJobProgressAsync(DownloadJob job)
    {
        try { await _dashboardEvents.JobProgress(job); }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to broadcast job {JobId}.", job.JobId); }
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
        lock (job.PlaylistLock)
        {
            if (job.State != JobState.QUEUED) return;
            _activeJobTokens[jobId] = jobCts;
            job.State = JobState.DOWNLOADING;
        }
        var token = jobCts.Token;

        try
        {
            token.ThrowIfCancellationRequested();
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

            // Always observe both pipe readers, including cancellation, before disposing
            // the process. Shutdown must not leave readers running against disposed handles.
            try
            {
                await process.WaitForExitAsync(token);
            }
            finally
            {
                try { await Task.WhenAll(outputTask, errorTask); }
                finally
                {
                    if (token.IsCancellationRequested)
                        await process.WaitForExitAsync(CancellationToken.None);
                }
            }

            // التحقق من الفشل أو الإكمال فقط إذا لم يتم الإلغاء
            if (!token.IsCancellationRequested)
            {
                if (process.ExitCode != 0 && job.State != JobState.FAILED)
                {
                    job.State = JobState.FAILED;
                    job.Message = BuildFailureMessage(
                        errorBuffer.ToString(),
                        $"Python engine exited with code {process.ExitCode}.");
                    job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Percent = -1,
                        DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
                        TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
                        SpeedBytesPerSecond = -1,
                        EtaSeconds = -1,
                        Timestamp = DateTime.UtcNow
                    };
                    await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
                    AppendTransferEvent(job, TransferEventType.JobFailed, null, job.Message);
                    PersistJob(job);

                    await _dashboardEvents.JobProgress(job);
                }

                if (job.State != JobState.COMPLETED && job.State != JobState.FAILED)
                {
                    job.State = JobState.FAILED;
                    job.Message ??= "The Python engine finished without a final result.";
                    job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Percent = -1,
                        DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
                        TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
                        SpeedBytesPerSecond = -1,
                        EtaSeconds = -1,
                        Timestamp = DateTime.UtcNow
                    };
                    await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
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
            job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
            {
                JobId = job.JobId,
                Status = "CANCELED",
                Percent = -1,
                DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
                TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
                SpeedBytesPerSecond = -1,
                EtaSeconds = -1,
                Timestamp = DateTime.UtcNow
            };
            await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
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
            job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
            {
                JobId = job.JobId,
                Status = "FAILED",
                Percent = -1,
                DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
                TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
                SpeedBytesPerSecond = -1,
                EtaSeconds = -1,
                Timestamp = DateTime.UtcNow
            };
            await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
            AppendTransferEvent(job, TransferEventType.JobFailed, null, job.Message);
            PersistJob(job);
            await _dashboardEvents.JobProgress(job);
            _playlistEventBus.Complete(job.JobId);
        }
        finally
        {
            // تنظيف الـ Token من القاموس بعد انتهاء المهمة أو إلغائها
            _activeJobTokens.TryRemove(jobId, out _);
            _lastConsolePercent.TryRemove(jobId, out _);
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

            // Progress is already exposed through SignalR. Avoid flushing the rolling log
            // for every progress tick; stage/result diagnostics remain at information level.
            if (line.Contains("JOB_PROGRESS|", StringComparison.Ordinal))
                _logger.LogDebug("Python stdout [{JobId}]: {Line}", job.JobId, line);
            else
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

            const int maxErrorCharacters = 64 * 1024;
            if (line.Length > maxErrorCharacters) line = line[^maxErrorCharacters..];
            if (buffer.Length + line.Length > maxErrorCharacters)
                buffer.Remove(0, Math.Min(buffer.Length, buffer.Length + line.Length - maxErrorCharacters));
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
            // FORMAT: JOB_PROGRESS|jobId|Status|Percent|Downloaded|Total|Speed|Eta
            var parts = progressLine.Split('|', 8);
            if (parts.Length >= 8)
            {
                if (double.TryParse(
                        parts[3].Trim().TrimEnd('%'),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var percent) &&
                    long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloaded) &&
                    long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) &&
                    double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) &&
                    long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eta))
                {
                    var isProcessing = string.Equals(parts[2], "PROCESSING", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(parts[2], "SEGMENTING", StringComparison.OrdinalIgnoreCase);
                    if (eta < 0 && speed > 0 && total > downloaded)
                    {
                        // Downloads use bytes and B/s. FFmpeg uses microseconds
                        // and reports a playback-speed multiplier (for example
                        // 2.4x), so calculate those two cases separately.
                        var remaining = total - downloaded;
                        eta = isProcessing
                            ? (long)Math.Ceiling(remaining / 1_000_000d / speed)
                            : (long)Math.Ceiling(remaining / speed);
                    }

                    job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
                    {
                        JobId = job.JobId,
                        Status = parts[2],
                        Percent = percent,
                        DownloadedBytes = downloaded,
                        TotalBytes = total,
                        SpeedBytesPerSecond = speed,
                        EtaSeconds = eta,
                        Timestamp = DateTime.UtcNow
                    };
                    
                    job.ProgressPercentage = percent > 0 ? $"{percent:F1}%" : "0%";
                    job.Speed = speed > 0
                        ? (string.Equals(parts[2], "PROCESSING", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(parts[2], "SEGMENTING", StringComparison.OrdinalIgnoreCase)
                            ? $"{speed:F2}x"
                            : $"Internet {FormatTransferRate(speed)}")
                        : string.Empty;
                    job.Eta = eta >= 0 ? FormatEta(eta) : "Calculating...";

                    // Keep the desktop/terminal host useful as well as the
                    // browser dashboard. Throttle output so fast downloads do
                    // not flood the console window.
                    var previousPercent = _lastConsolePercent.GetOrAdd(job.JobId, -1);
                    if (percent >= 100 || percent - previousPercent >= 1 || previousPercent < 0)
                    {
                        _lastConsolePercent[job.JobId] = percent;
                        WriteConsoleDetails(
                            job,
                            parts[2],
                            $"{percent:F1}%",
                            job.Speed,
                            job.Eta);
                    }

                    await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
                    await _dashboardEvents.JobProgress(job);
                }
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
            if (!string.Equals(stage, "COMPLETED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(stage, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                job.Speed = string.Empty;
                job.Eta = "Calculating...";
            }
            WriteConsoleDetails(job, stage, null, null, null, job.Message);
            PersistJob(job);
            
            // update and broadcast current progress with PROCESSING state
            job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
            {
                JobId = job.JobId,
                Status = stage,
                Percent = -1,
                DownloadedBytes = 0,
                TotalBytes = 0,
                SpeedBytesPerSecond = -1,
                EtaSeconds = -1,
                Timestamp = DateTime.UtcNow
            };
            await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);

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
                    job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
                    {
                        JobId = job.JobId,
                        Status = "COMPLETED",
                        Percent = 100,
                        DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
                        TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
                        SpeedBytesPerSecond = -1,
                        EtaSeconds = -1,
                        Timestamp = DateTime.UtcNow
                    };
                    await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
                    AppendTransferEvent(job, TransferEventType.JobCompleted, null, job.Message);
                    _playlistEventBus.Complete(job.JobId);
                    TriggerCompletionNotification(job);
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
                
                job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
                {
                    JobId = job.JobId,
                    Status = "COMPLETED",
                    Percent = 100,
                    DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
                    TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
                    SpeedBytesPerSecond = -1,
                    EtaSeconds = -1,
                    Timestamp = DateTime.UtcNow
                };
                await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
                AppendTransferEvent(job, TransferEventType.JobCompleted, null, job.Message);
                TriggerCompletionNotification(job);
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

    private static void WriteConsoleDetails(
        DownloadJob job,
        string stage,
        string? progress,
        string? speed,
        string? eta,
        string? message = null)
    {
        try
        {
            var id = job.JobId.Length > 8 ? job.JobId[..8] : job.JobId;
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(progress)) details.Add(progress);
            if (!string.IsNullOrWhiteSpace(speed)) details.Add($"Speed {speed}");
            if (!string.IsNullOrWhiteSpace(eta)) details.Add($"ETA {eta}");
            if (!string.IsNullOrWhiteSpace(message)) details.Add(message);

            lock (ConsoleOutputLock)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [{id}] {stage}: {string.Join(" | ", details)}");
            }
        }
        catch
        {
            // Console output is diagnostic only and must never affect a job.
        }
    }

    private static string FormatEta(long seconds)
    {
        if (seconds < 0) return "Calculating...";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string FormatTransferRate(double bytesPerSecond)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        return bytesPerSecond >= gb ? $"{bytesPerSecond / gb:F2} GB/s" :
               bytesPerSecond >= mb ? $"{bytesPerSecond / mb:F2} MB/s" :
               bytesPerSecond >= kb ? $"{bytesPerSecond / kb:F1} KB/s" :
               $"{bytesPerSecond:F0} B/s";
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
                TransferJson = JsonSerializer.Serialize(BuildTransferSnapshot(job)),
                ThumbnailUrl = job.ThumbnailUrl
                ,Title = job.OriginalTitle
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
        var contentHash = fileInfo.Exists ? ComputeSha256(filePath) : string.Empty;

        return new PlaylistItemResult
        {
            ItemId = ComputeStableItemId(contentHash, job.JobId, index),
            Index = index,
            FileName = fileName,
            Title = originalTitle ?? fileName,
            State = TransferState.Ready,
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            Sha256 = contentHash,
            CreatedUtc = fileInfo.Exists ? fileInfo.CreationTimeUtc : DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            Sequence = NextEventSequence(job)
        };
    }

    private static string ComputeStableItemId(string contentHash, string jobId, int index)
    {
        if (!string.IsNullOrEmpty(contentHash))
        {
            var itemBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{contentHash}:{index}"));
            return Convert.ToHexString(itemBytes).ToLowerInvariant();
        }

        var hashSource = $"{jobId}:{index}".ToLowerInvariant();
        var fallbackBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashSource));
        return Convert.ToHexString(fallbackBytes).ToLowerInvariant();
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.SequentialScan);
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
                protectedIds,
                _keepDownloads);

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
        DownloadJob? current = null;
        foreach (var entry in _jobs)
        {
            var job = entry.Value;
            if (IsRunning(job.State) && (current is null || job.CreatedAt > current.CreatedAt))
                current = job;
        }
        return current;
    }

    public QueueStatistics GetStatistics()
    {
        var statistics = new QueueStatistics();
        foreach (var entry in _jobs)
        {
            var state = entry.Value.State;
            statistics.TotalJobs++;
            if (state == JobState.QUEUED) statistics.QueuedJobs++;
            else if (IsRunning(state)) statistics.RunningJobs++;
            else if (state == JobState.COMPLETED) statistics.CompletedJobs++;
            else if (state == JobState.FAILED) statistics.FailedJobs++;
        }
        return statistics;
    }

    private static bool IsRunning(JobState state) => state is
        JobState.DOWNLOADING or JobState.PROCESSING or JobState.SEGMENTING or
        JobState.SEPARATING_VOCALS or JobState.MERGING_AUDIO;

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

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);

    private void TriggerCompletionNotification(DownloadJob job)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBeep(0x00000040); // MB_ICONASTERISK (Default Windows Notification Sound)
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to play Windows completion sound.");
        }
    }
}
