using System.Text.Json.Serialization;

namespace NebulaServer.Models.Jobs;

public sealed class TransferEventRecord
{
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("type")]
    public TransferEventType Type { get; set; }

    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("manifestVersion")]
    public long ManifestVersion { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
