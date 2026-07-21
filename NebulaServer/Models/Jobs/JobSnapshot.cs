namespace NebulaServer.Models.Jobs;

public sealed class JobSnapshot
{
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public double Progress { get; set; }

    public string? OutputPath { get; set; }

    public DateTime CreatedAt { get; set; }

    public string RequestJson { get; set; } = string.Empty;

    public string TransferJson { get; set; } = string.Empty;
}
