using System;

namespace NebulaServer.Models.Jobs;

public sealed class DownloadProgress
{
    public string JobId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public double Percent { get; set; } = -1;

    public long DownloadedBytes { get; set; }

    public long TotalBytes { get; set; }

    public double SpeedBytesPerSecond { get; set; } = -1;

    public long EtaSeconds { get; set; } = -1;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
