namespace NebulaServer.Settings;

public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    public double HeartbeatIntervalMinutes { get; set; }

    public int ValidationIntervalHours { get; set; } = 24;

    public int GracePeriodDays { get; set; } = 7;

    public int OfflineGraceExtensionHours { get; set; } = 24;
}
