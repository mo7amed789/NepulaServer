namespace NebulaServer.Models.Pairing
{
    public sealed class PairingQrPayload
    {
        public int Schema { get; set; } = 1;

        public Guid ServerId { get; set; }

        public string PairingKey { get; set; } = "";

        public string DeviceId { get; set; } = "";

        public string LocalIp { get; set; } = "";

        public int Port { get; set; }

        public string? PublicUrl { get; set; }

        public bool IsNgrokRunning { get; set; }

        public string Version { get; set; } = "";

        public DateTime TimestampUtc { get; set; }
    }
}
