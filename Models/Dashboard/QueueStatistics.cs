namespace NebulaServer.Models.Dashboard;

public sealed class QueueStatistics
{
    public int QueuedJobs { get; set; }

    public int RunningJobs { get; set; }

    public int CompletedJobs { get; set; }

    public int FailedJobs { get; set; }

    public int TotalJobs { get; set; }
}