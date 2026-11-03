using System;

namespace NebulaServer.Models.Media;

public sealed class MediaFormat
{
    public string Id { get; init; } = default!;
    public string Url { get; init; } = default!;
    public string Extension { get; init; } = default!;
    public string? Quality { get; init; }
    public long? Size { get; init; }
    public bool HasAudio { get; init; }
    public bool HasVideo { get; init; }
}
