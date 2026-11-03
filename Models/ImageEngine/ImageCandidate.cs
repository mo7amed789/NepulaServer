using System;

namespace NebulaServer.Models.ImageEngine
{
    public sealed class ImageCandidate
    {
        public string Url { get; init; } = null!;
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string? MimeType { get; init; }
        public long? FileSize { get; init; }
        public string Source { get; init; } = null!;
        public int Score { get; set; }
        
        // Sometimes useful to keep the raw HTML or srcset snippet for debugging
        public string? RawSourceSnippet { get; init; }
    }
}
