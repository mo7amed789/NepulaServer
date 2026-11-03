namespace NebulaServer.Models.Auth;

public sealed class PairAuthRequest
{
    public int Schema { get; set; } = 1;

    public Guid ServerId { get; set; }

    public string PairingKey { get; set; } = string.Empty;

    public string? PublicUrl { get; set; }
}
