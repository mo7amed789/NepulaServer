namespace NebulaServer.Models
{
    public sealed class HeartbeatResponse
    {
        public string? Status { get; set; }

        public DateTime? CurrentServerTimeUtc { get; set; }
    }
}
