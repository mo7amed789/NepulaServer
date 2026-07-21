using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NebulaServer.Services.Licensing;

/// <summary>
/// Windows-specific <see cref="IMachineFingerprint"/> implementation.
/// Derives a stable fingerprint purely from hardware identifiers: CPU,
/// motherboard, BIOS, system UUID and system-disk serial. Deliberately
/// excludes machine name (user-renameable) and Windows product id (not a
/// hardware identifier, changes on reinstall).
/// Never throws: any unavailable value is simply omitted from the hash input.
/// The computed hash is cached for the lifetime of the instance.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineFingerprint : IMachineFingerprint
{
    // Bump this whenever the fingerprint algorithm/inputs change, so a
    // license service can detect and handle fingerprints produced by an
    // older version instead of silently treating them as a hardware change.
    private const int FingerprintVersion = 1;

    private readonly ILogger<WindowsMachineFingerprint> _logger;
    private readonly Lazy<string> _cachedHash;

    public WindowsMachineFingerprint(ILogger<WindowsMachineFingerprint> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachedHash = new Lazy<string>(ComputeFingerprint, isThreadSafe: true);
    }

    /// <inheritdoc />
    public string GetMachineHash()
    {
        return _cachedHash.Value;
    }
    private string ComputeFingerprint()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);

        AddIfPresent(values, "BIOS", GetBiosSerial());
        AddIfPresent(values, "BOARD", GetMotherboardSerial());
        AddIfPresent(values, "CPU", GetCpuId());
        AddIfPresent(values, "DISK", GetDiskSerial());
        AddIfPresent(values, "UUID", GetSystemUuid());

        var builder = new StringBuilder();
        builder.Append("VERSION:").Append(FingerprintVersion).Append('\n');

        foreach (var pair in values)
        {
            builder.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
        }

        return ComputeSha256(builder.ToString());
    }

    private static void AddIfPresent(IDictionary<string, string> values, string key, string rawValue)
    {
        var normalized = Normalize(rawValue);
        if (!string.IsNullOrEmpty(normalized))
        {
            values[key] = normalized;
        }
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .Replace(".", string.Empty);
    }

    // ---------------------------------------------------------------------
    // Individual hardware identifier readers.
    // Each one returns string.Empty on failure and never throws.
    // ---------------------------------------------------------------------

    private string GetCpuId() =>
        QuerySingleWmiValue("Win32_Processor", "ProcessorId");

    private string GetMotherboardSerial() =>
        QuerySingleWmiValue("Win32_BaseBoard", "SerialNumber");

    private string GetBiosSerial() =>
        QuerySingleWmiValue("Win32_BIOS", "SerialNumber");

    private string GetSystemUuid() =>
        QuerySingleWmiValue("Win32_ComputerSystemProduct", "UUID");

    private string GetDiskSerial()
    {
        try
        {
            var systemDrive = GetSystemDriveLetter();
            var partitionDeviceId = GetPartitionDeviceId(systemDrive);

            return string.IsNullOrEmpty(partitionDeviceId)
                ? string.Empty
                : GetDiskSerialFromPartition(partitionDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read disk serial number.");
            return string.Empty;
        }
    }

    private static string GetSystemDriveLetter()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        return root?.TrimEnd('\\') ?? "C:";
    }

    private string GetPartitionDeviceId(string systemDrive)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{systemDrive}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            using var results = searcher.Get();

            foreach (ManagementBaseObject partition in results)
            {
                using (partition)
                {
                    return partition["DeviceID"]?.ToString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve system partition.");
        }

        return string.Empty;
    }

    private string GetDiskSerialFromPartition(string partitionDeviceId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionDeviceId}'}} " +
                "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

            using var results = searcher.Get();

            foreach (ManagementBaseObject disk in results)
            {
                using (disk)
                {
                    var serial = disk["SerialNumber"]?.ToString();
                    return !string.IsNullOrWhiteSpace(serial) ? serial : string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read disk drive serial number.");
        }

        return string.Empty;
    }

    private string QuerySingleWmiValue(string wmiClass, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT {propertyName} FROM {wmiClass}");

            using var results = searcher.Get();

            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var value = obj[propertyName]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query {WmiClass} for {Property}.", wmiClass, propertyName);
        }

        return string.Empty;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }
}