namespace NebulaServer.Models;

public class NgrokStatus
{
    public bool IsInstalled { get; set; }

    public bool IsConfigured { get; set; }

    public bool IsRunning { get; set; }

    public string? PublicUrl { get; set; }

    public string? Error { get; set; }
}