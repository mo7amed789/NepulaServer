using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NebulaServer.Services.Licensing;

/// <summary>
/// Stable per-machine identifier sent to the licensing server as
/// <c>machineHash</c>. The server binds a license to this value, so it must not
/// change between runs or after a reboot.
/// </summary>
public static class MachineFingerprint
{
    public static string Compute()
    {
        var parts = new List<string>
        {
            ReadMachineGuid(),
            Environment.MachineName,
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty,
            Environment.OSVersion.VersionString
        };

        var material = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrWhiteSpace(material))
            material = "nebulaserver";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
