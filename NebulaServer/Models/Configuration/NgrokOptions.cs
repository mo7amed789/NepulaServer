namespace NebulaServer.Models.Configuration;

public class NgrokOptions
{
    public const string SectionName = "Ngrok";

    public int ApiPort { get; set; } = 4040;

    public int TunnelPort { get; set; } = 5000;
}
