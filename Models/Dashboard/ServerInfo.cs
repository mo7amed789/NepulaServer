namespace NebulaServer.Models.Dashboard;

public class ServerInfo
{
    public string Name { get; set; } = "Nebula Server";

    public string Version { get; set; } = "";

    public bool IsRunning { get; set; }

    public DateTime StartedAt { get; set; }

    public TimeSpan Uptime { get; set; }
}