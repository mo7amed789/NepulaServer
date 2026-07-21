namespace NebulaServer.Models.Licensing;

public sealed class ActivateLicenseRequest
{
    public string LicenseKey { get; init; } = string.Empty;
}