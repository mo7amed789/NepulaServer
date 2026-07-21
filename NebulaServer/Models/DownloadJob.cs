using System.Collections.Concurrent;
using NebulaServer.Models.Jobs;

namespace NebulaServer.Models;

public class DownloadJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    public JobRequest Request { get; set; } = new();

    public JobState State { get; set; } = JobState.QUEUED;

    public string ProgressPercentage { get; set; } = "0%";

    public string Speed { get; set; } = string.Empty;

    public string Eta { get; set; } = string.Empty;

    public string? OutputFileName { get; set; }

    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // 🌟 السطر السحري لحفظ رابط الصورة المصغرة داخل كائن الذاكرة بالسيرفر
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string? OriginalTitle { get; internal set; }
    public ConcurrentDictionary<int, PlaylistItemResult> CompletedItems { get; } = new();
    public ConcurrentDictionary<int, byte> DeliveredPlaylistItemIndices { get; } = new();
    public ConcurrentDictionary<long, TransferEventRecord> Events { get; } = new();
    public object PlaylistLock { get; } = new();
    public long ManifestVersion { get; set; }
    public long EventSequence { get; set; }
}
