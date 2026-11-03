using System;
using System.IO;
using Microsoft.Win32;

namespace NebulaServer.Helpers;

public static class StartupHelper
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "Tanzeel";

    public static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
        {
            return processPath;
        }

        var baseExe = Path.Combine(AppContext.BaseDirectory, "NebulaServer.exe");
        if (File.Exists(baseExe))
        {
            return baseExe;
        }

        return Path.Combine(AppContext.BaseDirectory, "Tanzeel.exe");
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, false);
            var value = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the Windows Run entry pointed at the executable that is actually
    /// running. This repairs stale publish/temp paths after an application
    /// update without enabling startup for users who turned it off.
    /// </summary>
    public static bool RepairStartupRegistrationIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, true);
            if (key?.GetValue(AppValueName) is not string existing || string.IsNullOrWhiteSpace(existing))
                return false;

            var expected = $"\"{GetExecutablePath()}\" --background";
            if (string.Equals(existing.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return false;

            key.SetValue(AppValueName, expected, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunSubKey, true);
            if (key == null)
                return false;

            if (enable)
            {
                var exePath = GetExecutablePath();
                var command = $"\"{exePath}\" --background";
                key.SetValue(AppValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppValueName, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
