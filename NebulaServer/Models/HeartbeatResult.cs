namespace NebulaServer.Models
{
    public sealed class HeartbeatResult
    {
        public bool Success { get; set; }

        public string? Message { get; set; }

        public string? ErrorCode { get; set; }

        public string? Status { get; set; }

        public DateTime? CurrentServerTimeUtc { get; set; }
    }
}
