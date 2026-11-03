namespace NebulaServer.Services.Server;

public class ServerState
{
    public string Version { get; set; } = "1.0.0";

    public bool IsServerRunning { get; set; }

    public bool IsPythonReady { get; set; }

    public string PythonVersion { get; set; } = "";

    public string LocalIp { get; set; } = "";

    public int Port { get; set; }

    public string DeviceId { get; set; } = "";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public bool IsLicensed { get; set; }

    public string LicenseKey { get; set; } = "";

    public ServerState Clone()
    {
        return new ServerState
        {
            IsLicensed = IsLicensed,
            LicenseKey = LicenseKey,
            LocalIp = LocalIp,
            Port = Port,
            DeviceId = DeviceId,
            LastUpdated = LastUpdated
        };
    }
}