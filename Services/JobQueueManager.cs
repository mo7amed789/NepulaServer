using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaServer.Models;
using NebulaServer.Models.Dashboard;
using NebulaServer.Models.Jobs;
using NebulaServer.Services.Jobs;

namespace NebulaServer.Services;

public class JobQueueManager : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ArtifactRetention = TimeSpan.FromHours(24);
    private static readonly object ConsoleOutputLock = new();

    // Coalesce SignalR progress broadcasts and intermediate playlist persistence
    // so fast downloads don't flood UI clients or stall pipe reading on SQLite sync-writes.
    private static readonly long ProgressBroadcastIntervalTicks = TimeSpan.FromMilliseconds(250).Ticks;
    private static readonly long ItemPersistIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

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

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();
    private readonly ConcurrentDictionary<string, double> _lastConsolePercent = new();
    private readonly ConcurrentDictionary<string, long> _lastProgressBroadcastTicks = new();
    private readonly ConcurrentDictionary<string, long> _lastItemPersistTicks = new();

    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private readonly int _workerCount;
    private readonly bool _keepDownloads;

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
            request.Url = StorageService.NormalizeInputUrl(request.Url);

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
        if (_jobs.TryRemove(id, out _))
            _logger.LogInformation("Job {JobId} removed from queue memory.", id);
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
        TransferManifestHelper.RestoreSnapshot(job, snapshot);
        PersistJob(job);
    }

    public bool TrySetPlaylistItemState(DownloadJob job, int index, TransferState state)
    {
        if (!job.CompletedItems.TryGetValue(index, out var item))
            return false;

        item.State = state;
        TransferManifestHelper.BumpManifestVersion(job);
        if (state == TransferState.Completed)
            AppendAndPersistEvent(job, TransferEventType.ItemCompleted, item, $"Item {index} transfer completed.");
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

    // =========================================================================
    // Background Loop
    // =========================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
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
    // Job Execution
    // =========================================================================

    private async Task ProcessJob(string jobId, CancellationToken globalToken)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

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
            using var processKiller = token.Register(() =>
            {
                _logger.LogWarning("Killing python process for {JobId}", jobId);
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kill failed for {JobId}", jobId);
                }
            });

            var errorBuffer = new StringBuilder();
            var outputTask = ReadStandardOutputAsync(process, job, token);
            var errorTask = ReadStandardErrorAsync(process, job, errorBuffer, token);

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

            if (!token.IsCancellationRequested)
            {
                if (process.ExitCode != 0 && job.State != JobState.FAILED)
                {
                    var failMessage = EngineProtocolParser.BuildFailureMessage(
                        errorBuffer.ToString(),
                        $"Python engine exited with code {process.ExitCode}.");
                    await TransitionTerminalStateAsync(job, JobState.FAILED, "FAILED", failMessage, TransferEventType.JobFailed);
                }
                else if (job.State != JobState.COMPLETED && job.State != JobState.FAILED)
                {
                    var message = job.Message ?? "The Python engine finished without a final result.";
                    await TransitionTerminalStateAsync(job, JobState.FAILED, "FAILED", message, TransferEventType.JobFailed);
                }

                if (job.State is JobState.COMPLETED or JobState.FAILED or JobState.CANCELED)
                    _playlistEventBus.Complete(job.JobId);

                _logger.LogInformation(
                    "Job {JobId} finished with state {State}. Message: {Message}",
                    job.JobId, job.State, job.Message);
            }
        }
        catch (OperationCanceledException)
        {
            await TransitionTerminalStateAsync(job, JobState.CANCELED, "CANCELED", "Job was canceled by user.", TransferEventType.JobCanceled);
            _playlistEventBus.Complete(job.JobId);
            _logger.LogInformation("Job {JobId} was canceled gracefully.", job.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", job.JobId);
            await TransitionTerminalStateAsync(job, JobState.FAILED, "FAILED", ex.Message, TransferEventType.JobFailed);
            _playlistEventBus.Complete(job.JobId);
        }
        finally
        {
            _activeJobTokens.TryRemove(jobId, out _);
            _lastConsolePercent.TryRemove(jobId, out _);
            _lastProgressBroadcastTicks.TryRemove(jobId, out _);
            _lastItemPersistTicks.TryRemove(jobId, out _);
        }
    }

    private async Task TransitionTerminalStateAsync(
        DownloadJob job,
        JobState state,
        string status,
        string message,
        TransferEventType eventType,
        double percent = -1)
    {
        job.State = state;
        job.Message = message;
        job.CurrentProgress = new NebulaServer.Models.Jobs.DownloadProgress
        {
            JobId = job.JobId,
            Status = status,
            Percent = percent,
            DownloadedBytes = job.CurrentProgress?.DownloadedBytes ?? 0,
            TotalBytes = job.CurrentProgress?.TotalBytes ?? 0,
            SpeedBytesPerSecond = -1,
            EtaSeconds = -1,
            Timestamp = DateTime.UtcNow
        };
        await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
        AppendAndPersistEvent(job, eventType, null, message);
        PersistJob(job);
        await _dashboardEvents.JobProgress(job);
    }

    // =========================================================================
    // stdout / stderr Readers & Protocol Handling
    // =========================================================================

    private async Task ReadStandardOutputAsync(Process process, DownloadJob job, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.Contains("JOB_PROGRESS|", StringComparison.Ordinal))
                _logger.LogDebug("Python stdout [{JobId}]: {Line}", job.JobId, line);
            else
                _logger.LogInformation("Python stdout [{JobId}]: {Line}", job.JobId, line);

            await ParseEngineMessageAsync(job, line);
        }
    }

    private async Task ReadStandardErrorAsync(Process process, DownloadJob job, StringBuilder buffer, CancellationToken token)
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

    private async Task ParseEngineMessageAsync(DownloadJob job, string line)
    {
        if (EngineProtocolParser.TryParseProgress(line, job.JobId, out var progressUpdate))
        {
            job.CurrentProgress = progressUpdate.Progress;
            job.ProgressPercentage = progressUpdate.ProgressPercentage;
            job.Speed = progressUpdate.SpeedText;
            job.Eta = progressUpdate.EtaText;

            var percent = progressUpdate.Progress.Percent;
            var previousPercent = _lastConsolePercent.GetOrAdd(job.JobId, -1);
            if (percent >= 100 || percent - previousPercent >= 1 || previousPercent < 0)
            {
                _lastConsolePercent[job.JobId] = percent;
                WriteConsoleDetails(job, progressUpdate.Progress.Status, $"{percent:F1}%", job.Speed, job.Eta);
            }

            if (!Throttled(_lastProgressBroadcastTicks, job.JobId, ProgressBroadcastIntervalTicks, percent >= 100))
            {
                await _dashboardEvents.DownloadProgressChanged(job.CurrentProgress);
                await _dashboardEvents.JobProgress(job);
            }
            return;
        }

        if (EngineProtocolParser.TryParseStage(line, out var stage, out var details))
        {
            if (string.Equals(stage, "ITEM_COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                if (job.Request.IsPlaylist && !string.IsNullOrWhiteSpace(details))
                {
                    job.Message = $"Item ready: {details}";
                    if (!Throttled(_lastItemPersistTicks, job.JobId, ItemPersistIntervalTicks, force: false))
                        PersistJob(job);
                    if (!Throttled(_lastProgressBroadcastTicks, job.JobId, ProgressBroadcastIntervalTicks, force: false))
                        await _dashboardEvents.JobProgress(job);
                }
                return;
            }

            job.State = EngineProtocolParser.MapStageToState(stage);
            job.Message = string.IsNullOrWhiteSpace(details)
                ? EngineProtocolParser.BuildDefaultStageMessage(stage, job.Request.IsPlaylist)
                : details;

            if (!string.Equals(stage, "COMPLETED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(stage, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                job.Speed = string.Empty;
                job.Eta = "Calculating...";
            }

            WriteConsoleDetails(job, stage, null, null, null, job.Message);
            PersistJob(job);

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

        if (EngineProtocolParser.TryParseResult(line, out var outputName, out var originalTitle))
        {
            if (job.Request.IsPlaylist)
            {
                var isFinalSummary = string.Equals(outputName, job.JobId, StringComparison.OrdinalIgnoreCase);
                if (isFinalSummary)
                {
                    await TransitionTerminalStateAsync(job, job.State, "COMPLETED", "Playlist ready for download", TransferEventType.JobCompleted, percent: 100);
                    _playlistEventBus.Complete(job.JobId);
                    TriggerCompletionNotification();
                    return;
                }

                var fileName = Path.GetFileName(outputName);
                var indexPrefix = fileName.Split('_', 2)[0];
                if (int.TryParse(indexPrefix, out var idx) && !job.CompletedItems.ContainsKey(idx))
                {
                    var item = TransferManifestHelper.BuildPlaylistItem(
                        job, _storage.GetPlaylistDirectoryPath(job.JobId), fileName, idx, originalTitle);
                    job.CompletedItems[idx] = item;
                    TransferManifestHelper.BumpManifestVersion(job);
                    AppendAndPersistEvent(job, TransferEventType.ItemReady, item, $"Item ready: {fileName}");
                    _playlistEventBus.Publish(job.JobId, item);
                }

                job.Message = $"Playlist item ready: {fileName}";
                PersistJob(job);
                await _dashboardEvents.JobProgress(job);
            }
            else
            {
                job.OutputFileName = outputName;
                if (originalTitle != null) job.OriginalTitle = originalTitle;
                job.ProgressPercentage = "100%";
                job.Speed = string.Empty;
                job.Eta = string.Empty;
                await TransitionTerminalStateAsync(job, JobState.COMPLETED, "COMPLETED", "File ready for download", TransferEventType.JobCompleted, percent: 100);
                TriggerCompletionNotification();
            }
            return;
        }

        if (EngineProtocolParser.TryParseThumbnail(line, out var thumbnailUrl))
        {
            job.ThumbnailUrl = thumbnailUrl;
            await _dashboardEvents.JobProgress(job);
        }
    }

    // =========================================================================
    // Persistence, Cleanup & Diagnostics
    // =========================================================================

    private void PersistJob(DownloadJob job)
    {
        try
        {
            _jobStore.Save(new JobSnapshot
            {
                Id = job.JobId,
                Url = job.Request.Url,
                Status = job.State.ToString(),
                Progress = EngineProtocolParser.ParseProgressPercent(job.ProgressPercentage),
                OutputPath = job.OutputFileName,
                CreatedAt = job.CreatedAt,
                RequestJson = JsonSerializer.Serialize(job.Request),
                TransferJson = JsonSerializer.Serialize(TransferManifestHelper.BuildTransferSnapshot(job)),
                ThumbnailUrl = job.ThumbnailUrl,
                Title = job.OriginalTitle ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist job snapshot for {JobId}.", job.JobId);
        }
    }

    private void AppendAndPersistEvent(DownloadJob job, TransferEventType type, PlaylistItemResult? item, string? message)
    {
        TransferManifestHelper.AppendTransferEvent(job, type, item, message);
        PersistJob(job);
    }

    private static bool Throttled(ConcurrentDictionary<string, long> map, string jobId, long intervalTicks, bool force)
    {
        var now = DateTime.UtcNow.Ticks;
        if (force)
        {
            map[jobId] = now;
            return false;
        }
        var last = map.GetOrAdd(jobId, 0L);
        if (last != 0 && now - last < intervalTicks) return true;
        map[jobId] = now;
        return false;
    }

    private void RunScheduledCleanup()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCleanupUtc < CleanupInterval)
            return;

        _lastCleanupUtc = now;

        try
        {
            var protectedIds = _jobs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _storage.CleanupExpiredArtifacts(ArtifactRetention, protectedIds, _keepDownloads);
            _storage.ProcessScheduledPlaylistDeletion();
            TrimExpiredJobs(now.Subtract(ArtifactRetention));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup pass failed");
        }
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{id}] {stage}: {string.Join(" | ", details)}");
            }
        }
        catch
        {
            // Console output is diagnostic only and must never affect a job.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);

    private void TriggerCompletionNotification()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                MessageBeep(0x00000040);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to play Windows completion sound.");
        }
    }
}
