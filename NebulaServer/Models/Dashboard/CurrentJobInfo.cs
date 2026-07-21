namespace NebulaServer.Models.Dashboard;

public sealed class CurrentJobInfo
{
    public bool HasJob { get; set; }

    public string JobId { get; set; } = "";

    public string Title { get; set; } = "";

    public string State { get; set; } = "";

    public string Progress { get; set; } = "";

    public string Speed { get; set; } = "";

    public string Eta { get; set; } = "";

    public string ThumbnailUrl { get; set; } = "";
}