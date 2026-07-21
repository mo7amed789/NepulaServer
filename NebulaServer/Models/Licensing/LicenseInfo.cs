namespace NebulaServer.Models.Licensing;

public sealed class LicenseInfo
{
    public string LicenseKey { get; set; } = string.Empty;

    public string MachineHash { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string NebulaVersion { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime LastValidationUtc { get; set; }

    public DateTime? LastSuccessfulValidationUtc { get; set; }

    public DateTime? SignedAtUtc { get; set; }

    public DateTime? OfflineGraceUntilUtc { get; set; }

    public string SignatureAlgorithm { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;

    public bool Activated { get; set; }
}
