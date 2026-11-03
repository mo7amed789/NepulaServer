using System;
using System.Collections.Generic;

namespace NebulaServer.Models.Media;

public sealed class MediaResult
{
    public string Id { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string? Thumbnail { get; init; }
    public TimeSpan? Duration { get; init; }
    public string Platform { get; init; } = default!;

    public List<MediaFormat> Formats { get; init; } = new();
}
