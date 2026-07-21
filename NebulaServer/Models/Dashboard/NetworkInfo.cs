namespace NebulaServer.Models.Dashboard;

public class NetworkInfo
{
    public string LocalIp { get; set; } = "";

    public int Port { get; set; }

    public string LocalUrl { get; set; } = "";

    public string PublicUrl { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public bool RemoteAvailable { get; set; }
}