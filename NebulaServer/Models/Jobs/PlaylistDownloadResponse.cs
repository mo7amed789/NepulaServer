using System.Text.Json.Serialization;

namespace NebulaServer.Models.Jobs;

public sealed class PlaylistDownloadResponse
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("playlist")]
    public bool Playlist { get; set; } = true;

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("files")]
    public IReadOnlyList<PlaylistDownloadItem> Files { get; set; } = Array.Empty<PlaylistDownloadItem>();

    [JsonPropertyName("nextDownloadUrl")]
    public string? NextDownloadUrl { get; set; }
}

public sealed class PlaylistDownloadItem
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("nextDownloadUrl")]
    public string? NextDownloadUrl { get; set; }
}
