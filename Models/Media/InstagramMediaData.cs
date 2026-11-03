using System;
using System.Collections.Generic;
using NebulaServer.Models.Media;

namespace NebulaServer.Models.Media;

public class InstagramMediaData
{
    public string Id { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string? Thumbnail { get; init; }
    public TimeSpan? Duration { get; init; }
    public List<MediaFormat> Formats { get; init; } = new();
}
