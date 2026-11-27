using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;

namespace NebulaServer.Services.Jobs;

/// <summary>
/// Builds and updates playlist transfer manifests, item hashes, and transfer event sequences.
/// </summary>
internal static class TransferManifestHelper
{
    public static void RestoreSnapshot(DownloadJob job, TransferSnapshot snapshot)
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
    }

    public static TransferSnapshot BuildTransferSnapshot(DownloadJob job)
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

    public static PlaylistItemResult BuildPlaylistItem(
        DownloadJob job,
        string playlistDirectory,
        string fileName,
        int index,
        string? originalTitle)
    {
        var filePath = Path.Combine(playlistDirectory, fileName);
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

    public static long NextEventSequence(DownloadJob job)
    {
        lock (job.PlaylistLock)
        {
            job.EventSequence++;
            return job.EventSequence;
        }
    }

    public static void BumpManifestVersion(DownloadJob job)
    {
        lock (job.PlaylistLock)
        {
            job.ManifestVersion++;
        }
    }

    public static TransferEventRecord AppendTransferEvent(
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
            return evt;
        }
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
}
