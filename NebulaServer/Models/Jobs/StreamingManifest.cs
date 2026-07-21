using System.Text.Json.Serialization;

namespace NebulaServer.Models.Jobs;

public sealed class StreamingManifest
{
    [JsonPropertyName("version")]
    public long Version { get; set; }

    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("playlist")]
    public bool Playlist { get; set; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("completed")]
    public int Completed { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<StreamingManifestItem> Items { get; set; } = Array.Empty<StreamingManifestItem>();
}

public sealed class StreamingManifestItem
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

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("nextDownloadUrl")]
    public string? NextDownloadUrl { get; set; }
}
