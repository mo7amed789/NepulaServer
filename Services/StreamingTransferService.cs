using System.Collections.Concurrent;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;

namespace NebulaServer.Services;

public sealed class StreamingTransferService
{
    private readonly StorageService _storage;
    private readonly PlaylistEventBus _eventBus;
    private readonly ConcurrentDictionary<string, DownloadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileTransferSession> _activeTransfers = new(StringComparer.OrdinalIgnoreCase);

    public StreamingTransferService(StorageService storage, PlaylistEventBus eventBus)
    {
        _storage = storage;
        _eventBus = eventBus;
    }

    public DownloadSession GetOrCreateSession(string jobId, string? deviceId = null)
    {
        return _sessions.GetOrAdd(
            string.IsNullOrWhiteSpace(deviceId) ? jobId : $"{jobId}:{deviceId}",
            _ => new DownloadSession(jobId, deviceId));
    }

    public void RestoreSession(string jobId, IEnumerable<PlaylistItemResult> items, string? deviceId = null)
    {
        var session = GetOrCreateSession(jobId, deviceId);
        session.RestoreFromItems(items);
    }

    public void PublishReadyItem(string jobId, PlaylistItemResult item)
    {
        _eventBus.Publish(jobId, item);
    }

    public void CompleteJob(string jobId)
    {
        _eventBus.Complete(jobId);
        foreach (var key in _sessions.Keys.Where(key => key.Equals(jobId, StringComparison.OrdinalIgnoreCase) || key.StartsWith(jobId + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _sessions.TryRemove(key, out _);
        }
    }

    public void MarkSessionFailed(string jobId)
    {
        if (_sessions.TryGetValue(jobId, out var session))
        {
            session.MarkFailed();
        }

        _eventBus.Complete(jobId);
    }

    public async Task<FileTransferSession> OpenPlaylistItemAsync(
        DownloadJob job,
        PlaylistItemResult item,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_storage.GetPlaylistDirectoryPath(job.JobId), item.FileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found on server disk: {filePath}", filePath);

        var lease = await _storage.AcquireFileLockAsync(filePath, cancellationToken);
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var transfer = new FileTransferSession(
            sessionId,
            job.JobId,
            item.Index,
            filePath,
            ResolveContentType(filePath),
            stream,
            lease);

        _activeTransfers[transfer.SessionId] = transfer;
        return transfer;
    }

    public void CompleteTransfer(FileTransferSession transfer)
    {
        _activeTransfers.TryRemove(transfer.SessionId, out _);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var transfer in _activeTransfers.Values.ToArray())
        {
            try
            {
                await transfer.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // best effort during shutdown
            }
        }

        _activeTransfers.Clear();
        _sessions.Clear();
    }

    public StreamingManifest BuildManifest(DownloadJob job, string? deviceId = null)
    {
        var session = GetOrCreateSession(job.JobId, deviceId);
        var completedItems = job.CompletedItems.Values
            .OrderBy(item => item.Index)
            .ToList();
        var completedCount = completedItems.Count(item => item.State == TransferState.Completed);

        var items = completedItems.Select(item =>
        {
            session.Touch();

            return new StreamingManifestItem
            {
                Index = item.Index,
                FileName = item.FileName,
                Title = item.Title,
                State = session.IsDelivered(item.Index)
                    ? TransferState.Completed
                    : session.IsInFlight(item.Index)
                        ? TransferState.Transferring
                        : TransferState.Ready,
                DownloadUrl = $"/api/jobs/{job.JobId}/download/{item.Index}",
                NextDownloadUrl = completedItems.FirstOrDefault(i => i.Index > item.Index)?.Index is int nextIndex
                    ? $"/api/jobs/{job.JobId}/download/{nextIndex}"
                    : null
            };
        }).ToArray();

        return new StreamingManifest
        {
            Version = job.ManifestVersion,
            JobId = job.JobId,
            Playlist = job.Request.IsPlaylist,
            GeneratedAtUtc = DateTime.UtcNow,
            Completed = completedCount,
            Total = completedItems.Count,
            EventSequence = job.EventSequence,
            Items = items
        };
    }

    public async Task<PlaylistItemResult?> TryGetNextReadyItemAsync(
        DownloadJob job,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = GetOrCreateSession(job.JobId);
        session.Touch();

        var nextItem = job.CompletedItems.Values
            .OrderBy(item => item.Index)
            .FirstOrDefault(item => !session.IsDelivered(item.Index));

        if (nextItem is null)
            return null;

        await Task.Yield();
        return nextItem;
    }

    public static string ResolveContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            _ => "application/octet-stream"
        };
    }
}
