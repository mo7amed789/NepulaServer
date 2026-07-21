namespace NebulaServer.Models.Dashboard;

public class StorageInfo
{
    public string DownloadsFolder { get; set; } = "";

    public long FreeSpaceBytes { get; set; }

    public long TotalSpaceBytes { get; set; }
}