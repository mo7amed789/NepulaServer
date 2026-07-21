using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NebulaServer.Services;

public class SystemInfoService
{
    private readonly string _idFilePath;
    public string DeviceId { get; private set; } = string.Empty;
    public int Port { get; } = 5000;
    public string Version => "1.0.0";

    public SystemInfoService()
    {
        _idFilePath = Path.Combine(AppContext.BaseDirectory, "device_id.txt");
        InitializeDeviceId();
    }

    public string GetLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = ni.GetIPProperties();
                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.Address.ToString(); 
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
        if (File.Exists(_idFilePath))
        {
            DeviceId = File.ReadAllText(_idFilePath).Trim();
        }
        else
        {
            var shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
            DeviceId = $"NB-{shortGuid}";
            File.WriteAllText(_idFilePath, DeviceId);
        }
    }
}