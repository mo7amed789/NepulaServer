using System.Collections.Generic;

namespace NebulaServer.Services;

public class QualityOption
{
    public string Label { get; set; } = string.Empty;   // "1080", "720", "audio"
    public long? FilesizeBytes { get; set; }
    public string? FilesizeDisplay { get; set; }
}

public class PreviewData
{
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<QualityOption> AvailableQualities { get; set; } = new();
}
