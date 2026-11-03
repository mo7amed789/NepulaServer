using System.Text.Json.Serialization;

namespace NebulaServer.Models.Jobs;

public sealed class TransferSnapshot
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("manifestVersion")]
    public long ManifestVersion { get; set; }

    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; set; }

    [JsonPropertyName("items")]
    public List<TransferSnapshotItem> Items { get; set; } = new();

    [JsonPropertyName("events")]
    public List<TransferEventRecord> Events { get; set; } = new();

    [JsonPropertyName("updatedUtc")]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TransferSnapshotItem
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public TransferState State { get; set; } = TransferState.Queued;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("completedAt")]
    public DateTime CompletedAt { get; set; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }
}
