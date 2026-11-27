using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Services.Licensing;

namespace NebulaServer.Services.Core;

/// <summary>
/// Thin facade over <see cref="LicensingClient"/> kept for the existing call
/// sites (<c>POST /api/setup/license</c>, desktop activation dialog).
/// </summary>
public sealed class LicenseService
{
    private readonly LicensingClient _client;
    private readonly ILogger<LicenseService> _logger;

    public LicenseService(LicensingClient client, ILogger<LicenseService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool IsLicensingServerConfigured => _client.IsConfigured;

    public string MachineHash => _client.MachineHash;

    /// <summary>
    /// Validates a user-supplied key against NebulaLicensingServer, activating
    /// it on this machine first if it has never been bound.
    /// </summary>
    public async Task<bool> ValidateLicenseAsync(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return false;

        if (!_client.IsConfigured)
        {
            // Never treat an arbitrary user-provided string as a production
            // license unless the licensing server is configured.
            _logger.LogWarning("License key rejected: Licensing:BaseUrl is not configured.");
            return false;
        }

        var result = await _client.ActivateOrValidateAsync(licenseKey);
        _logger.LogInformation(
            "License check for {Key}: valid={Valid}, status={Status}, signature={Verified}, message={Message}",
            Mask(licenseKey), result.IsValid, result.Status, result.SignatureVerified, result.Message);

        return result.IsValid;
    }

    /// <summary>Full result, used by the desktop UI and the heartbeat service.</summary>
    public Task<LicenseCheckResult> CheckAsync(string licenseKey, CancellationToken ct = default) =>
        _client.ActivateOrValidateAsync(licenseKey, ct);

    public Task<LicenseCheckResult> ValidateOnlyAsync(string licenseKey, CancellationToken ct = default) =>
        _client.ValidateAsync(licenseKey, ct);

    public Task<LicenseCheckResult> HeartbeatAsync(string licenseKey, CancellationToken ct = default) =>
        _client.HeartbeatAsync(licenseKey, ct);

    internal static string Mask(string key) =>
        string.IsNullOrWhiteSpace(key) || key.Length < 8
            ? "****"
            : $"{key[..4]}****{key[^4..]}";
}
