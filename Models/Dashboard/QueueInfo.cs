namespace NebulaServer.Models.Dashboard;

public class QueueInfo
{
    public int RunningJobs { get; set; }

    public int QueuedJobs { get; set; }

    public int CompletedJobs { get; set; }

    public int FailedJobs { get; set; }
}