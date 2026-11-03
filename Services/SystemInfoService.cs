using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NebulaServer.Helpers;

namespace NebulaServer.Services;

public class SystemInfoService
{
    private readonly string _idFilePath;
    public string DeviceId { get; private set; } = string.Empty;
    public int Port { get; }
    public string Version => "1.0.0";

    public SystemInfoService(IConfiguration configuration)
    {
        Port = Math.Clamp(configuration.GetValue("Server:Port", 5000), 1, 65535);
        _idFilePath = Path.Combine(RuntimePaths.Data, "device_id.txt");
        InitializeDeviceId();
    }

    public string GetLocalIpAddress()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(ni => ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
                .ThenByDescending(ni => ni.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork));

            foreach (var ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = ni.GetIPProperties();
                var hasIpv4Gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                foreach (var ip in props.UnicastAddresses)
                {
                    var address = ip.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address) &&
                        !address.ToString().StartsWith("169.254.", StringComparison.Ordinal) &&
                        (hasIpv4Gateway || ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
                    {
                        return address.ToString();
                    }
                }
            }
        }
        catch
        {
        }
        return "127.0.0.1";
    }

    private void InitializeDeviceId()
    {
        Directory.CreateDirectory(RuntimePaths.Data);

        if (File.Exists(_idFilePath))
        {
            DeviceId = File.ReadAllText(_idFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(DeviceId))
            {
                return;
            }
        }

        var shortGuid = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        DeviceId = $"NB-{shortGuid}";
        File.WriteAllText(_idFilePath, DeviceId);
    }
}
