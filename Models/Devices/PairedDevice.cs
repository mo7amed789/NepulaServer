namespace NebulaServer.Models.Devices;

public sealed class PairedDevice
{
    public string DeviceId { get; set; } = "";

    public string DeviceName { get; set; } = "";

    public string Platform { get; set; } = "";

    public string AppVersion { get; set; } = "";

    public string IpAddress { get; set; } = "";

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }

    public bool IsOnline { get; set; }

    public bool IsDownloading { get; set; }
}