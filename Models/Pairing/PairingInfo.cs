namespace NebulaServer.Models.Pairing;

public sealed class PairingInfo
{
    /// <summary>
    /// Unique identifier for this Nebula Server installation.
    /// </summary>
    public Guid ServerId { get; set; }

    /// <summary>
    /// Secret key used by clients to authenticate.
    /// </summary>
    public string PairingKey { get; set; } = string.Empty;

    /// <summary>
    /// UTC creation date.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Last key rotation date.
    /// </summary>
    public DateTime LastRotationUtc { get; set; }
}