using NebulaServer.Models.Jobs;
using System.Text.Json.Serialization;

namespace NebulaServer.Models;

public class JobStatusResponse
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = "QUEUED";

    [JsonPropertyName("progressPercentage")]
    public string ProgressPercentage { get; set; } = "0%" ;

    [JsonPropertyName("speed")]
    public string Speed { get; set; } = string.Empty;

    [JsonPropertyName("eta")]
    public string Eta { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("outputFileName")]
    public string? OutputFileName { get; set; }

    [JsonPropertyName("completedItems")]
    public List<PlaylistItemResult> CompletedItems { get; set; } = new();

    [JsonPropertyName("manifestVersion")]
    public long ManifestVersion { get; set; }

    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; set; }

    [JsonPropertyName("completedCount")]
    public int CompletedCount { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    // 🌟 هذا هو السطر السحري المفقود الذي سيحل أزمة الصورة المصغرة غلوبالياً!
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; } = string.Empty;
}
