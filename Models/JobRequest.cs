using System.Text.Json.Serialization;

namespace NebulaServer.Models;

public class JobRequest
{
    [JsonPropertyName("Url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("Quality")]
    public string Quality { get; set; } = "720";

    [JsonPropertyName("Format")]
    public string Format { get; set; } = "mp4";

    [JsonPropertyName("AudioQuality")]
    public string AudioQuality { get; set; } = "128";

    [JsonPropertyName("FastMode")]
    public bool FastMode { get; set; } = true;

    [JsonPropertyName("Proxy")]
    public string? Proxy { get; set; }

    [JsonPropertyName("ExtractVocals")]
    public bool ExtractVocals { get; set; }

    [JsonPropertyName("SegmentStart")]
    public string? SegmentStart { get; set; }

    [JsonPropertyName("SegmentEnd")]
    public string? SegmentEnd { get; set; }


    [JsonPropertyName("IsPlaylist")]
    public bool IsPlaylist { get; set; }

    [JsonPropertyName("PlaylistStart")]
    public int? PlaylistStart { get; set; }

    [JsonPropertyName("PlaylistEnd")]
    public int? PlaylistEnd { get; set; }

    [JsonPropertyName("DataSaverMode")]
    public bool DataSaverMode { get; set; }

    [JsonPropertyName("MaxSpeedBytes")]
    public long? MaxSpeedBytes { get; set; }
}
