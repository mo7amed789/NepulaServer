namespace NebulaServer.Models.Pairing;

public sealed class PairingResponse
{
    public Guid ServerId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string LocalIp { get; set; } = string.Empty;

    public int Port { get; set; }

    public string? PublicUrl { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool IsNgrokRunning { get; set; }
}