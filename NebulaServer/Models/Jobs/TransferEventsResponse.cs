using System.Text.Json.Serialization;

namespace NebulaServer.Models.Jobs;

public sealed class TransferEventsResponse
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("after")]
    public long After { get; set; }

    [JsonPropertyName("latest")]
    public long Latest { get; set; }

    [JsonPropertyName("events")]
    public IReadOnlyList<TransferEventRecord> Events { get; set; } = Array.Empty<TransferEventRecord>();
}
