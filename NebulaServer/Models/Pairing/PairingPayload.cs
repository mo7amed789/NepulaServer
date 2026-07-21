namespace NebulaServer.Models.Pairing;

public sealed class PairingPayload
{
    /// <summary>
    /// Pairing protocol version.
    /// </summary>
    public int Schema { get; set; } = 1;

    /// <summary>
    /// Unique Nebula Server installation id.
    /// </summary>
    public Guid ServerId { get; set; }

    /// <summary>
    /// Device identifier.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Local IP address.
    /// </summary>
    public string LocalIp { get; set; } = string.Empty;

    /// <summary>
    /// Server port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Ngrok public URL.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Pairing key.
    /// </summary>
    public string PairingKey { get; set; } = string.Empty;

    /// <summary>
    /// Nebula Server version.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Whether ngrok is currently connected.
    /// </summary>
    public bool IsNgrokRunning { get; set; }

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}