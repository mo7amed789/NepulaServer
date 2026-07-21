namespace NebulaServer.Models.Licensing;

public sealed class ActivateResponse
{
    public bool Success { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string LicenseKey { get; init; } = string.Empty;

    public DateTime ExpiresAt { get; init; }

    public DateTime? SignedAtUtc { get; init; }

    public DateTime? OfflineGraceUntilUtc { get; init; }

    public string SignatureAlgorithm { get; init; } = string.Empty;

    public string Signature { get; init; } = string.Empty;
}
