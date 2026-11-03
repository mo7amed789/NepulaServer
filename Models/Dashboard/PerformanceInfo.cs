namespace NebulaServer.Models.Dashboard;

public class PerformanceInfo
{
    public double CpuUsage { get; set; }

    public double MemoryUsageMb { get; set; }

    public long WorkingSetBytes { get; set; }
}