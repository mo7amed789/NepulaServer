using System.Collections.Concurrent;
using NebulaServer.Models.Devices;

namespace NebulaServer.Services.Devices;

public sealed class DeviceRegistryService
{
    private readonly ConcurrentDictionary<string, PairedDevice> _devices = new();

    public IReadOnlyCollection<PairedDevice> GetAll()
        => _devices.Values.ToList();

    public int Count => _devices.Count;

    public void Register(
        string deviceId,
        string ip,
        string platform,
        string version)
    {
        _devices.AddOrUpdate(
            deviceId,
            _ => new PairedDevice
            {
                DeviceId = deviceId,
                DeviceName = deviceId,
                Platform = platform,
                AppVersion = version,
                IpAddress = ip,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                IsOnline = true
            },
            (_, device) =>
            {
                device.LastSeen = DateTime.UtcNow;
                device.IsOnline = true;
                device.IpAddress = ip;
                device.Platform = platform;
                device.AppVersion = version;
                return device;
            });
    }

    public void MarkOffline(TimeSpan timeout)
    {
        foreach (var device in _devices.Values)
        {
            device.IsOnline =
                DateTime.UtcNow - device.LastSeen < timeout;
        }
    }
}