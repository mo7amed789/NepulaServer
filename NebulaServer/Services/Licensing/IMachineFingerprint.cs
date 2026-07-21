namespace NebulaServer.Services.Licensing;

/// <summary>
/// Provides a stable, hardware-derived fingerprint for the current machine,
/// suitable for software licensing scenarios.
/// </summary>
public interface IMachineFingerprint
{
    /// <summary>
    /// Returns a deterministic, uppercase SHA-256 hex hash derived from stable
    /// hardware identifiers of the current machine. The value is cached after
    /// the first call and will not change across reboots, Windows Updates,
    /// network/IP changes, or a machine rename.
    /// </summary>
    string GetMachineHash();
}