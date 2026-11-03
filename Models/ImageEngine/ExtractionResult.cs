using System.Collections.Generic;

namespace NebulaServer.Models.ImageEngine
{
    public sealed class ExtractionResult
    {
        public string Title { get; init; } = "Unknown";
        public IReadOnlyList<ImageCandidate> Images { get; init; } = Array.Empty<ImageCandidate>();
    }
}
