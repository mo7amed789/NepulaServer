namespace NebulaServer.Services.Ngrok;

public class ServerState
{
    public string Version { get; set; } = "1.0.0";

    public bool IsServerRunning { get; set; }

    public bool IsPythonReady { get; set; }

    public string PythonVersion { get; set; } = "";
    public bool IsNgrokRunning { get; set; }

    public bool IsConfigured { get; set; }

    public string? PublicUrl { get; set; }

    public string LocalIp { get; set; } = "";

    public int Port { get; set; }

    public string DeviceId { get; set; } = "";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public ServerState Clone()
    {
        return new ServerState
        {
            IsNgrokRunning = IsNgrokRunning,
            IsConfigured = IsConfigured,
            PublicUrl = PublicUrl,
            LocalIp = LocalIp,
            Port = Port,
            DeviceId = DeviceId,
            LastUpdated = LastUpdated
        };
    }
}