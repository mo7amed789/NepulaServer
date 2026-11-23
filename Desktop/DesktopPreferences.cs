using System.Text.Json;
using NebulaServer.Helpers;

namespace NebulaServer.Desktop;

internal sealed class DesktopPreferences
{
    public bool CloseToTray { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool ClipboardAutoDownloadEnabled { get; set; }
    public string Format { get; set; } = "mp4";
    public string Quality { get; set; } = "720";
    public string AudioQuality { get; set; } = "128";
    public bool FastMode { get; set; } = true;
    public bool DarkMode { get; set; }
    public int Width { get; set; } = 1180;
    public int Height { get; set; } = 780;
    public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANZEEL_DATA_DIR"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanzeel")
            : RuntimePaths.Data, "desktop-preferences.json");

    public static DesktopPreferences Load()
    {
        try { return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
