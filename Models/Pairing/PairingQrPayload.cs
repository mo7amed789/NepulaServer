namespace NebulaServer.Models.Pairing
{
    public sealed class PairingQrPayload
    {
        public Guid ServerId { get; set; }
        public string PairingKey { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public int Port { get; set; }

    }
}