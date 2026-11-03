using System.Text.Json.Serialization;

namespace NebulaServer.Models.Jobs;

public sealed class PlaylistItemResult
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
