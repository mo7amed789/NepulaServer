using NebulaServer.Models.Devices;

namespace NebulaServer.Models.Dashboard;

public class DashboardResponse
{
    public ServerInfo Server { get; set; } = new();

    public RuntimeInfo Runtime { get; set; } = new();

    public NetworkInfo Network { get; set; } = new();

    public PerformanceInfo Performance { get; set; } = new();

    public QueueInfo Queue { get; set; } = new();
    public CurrentJobInfo CurrentJob { get; set; } = new();
    public StorageInfo Storage { get; set; } = new();
    public List<PairedDevice> Devices { get; set; } = new();

}