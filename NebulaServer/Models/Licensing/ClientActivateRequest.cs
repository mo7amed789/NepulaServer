namespace NebulaServer.Models.Licensing;

public sealed class ClientActivateRequest
{
    public required string LicenseKey { get; init; }

    public required string MachineHash { get; init; }

    public required string DeviceName { get; init; }

    public required string NebulaVersion { get; init; }

    public required string ServerId { get; init; }
}
