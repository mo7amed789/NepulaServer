namespace NebulaServer.Models.Licensing;

public sealed class ClientHeartbeatRequest
{
    public required string LicenseKey { get; init; }

    public required string MachineHash { get; init; }

    public required string DeviceName { get; init; }

    public required string NebulaVersion { get; init; }

    public required string ServerId { get; init; }
    public  DateTime CurrentTimeUtc { get; init; }
}
