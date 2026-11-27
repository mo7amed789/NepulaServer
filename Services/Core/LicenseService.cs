using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Services.Licensing;
using NebulaServer.Services.Server;

namespace NebulaServer.Services.Core;

/// <summary>
/// Central activation and validation facade over <see cref="LicensingClient"/>,
/// keeping <see cref="ServerStateService"/> and persisted state in sync.
/// </summary>
public sealed class LicenseService
{
    private readonly LicensingClient _client;
    private readonly ServerStateService _serverState;
    private readonly ServerStateFileService _stateFile;
    private readonly ILogger<LicenseService> _logger;

    public LicenseService(
        LicensingClient client,
        ServerStateService serverState,
        ServerStateFileService stateFile,
        ILogger<LicenseService> logger)
    {
        _client = client;
        _serverState = serverState;
        _stateFile = stateFile;
        _logger = logger;
    }

    public bool IsLicensingServerConfigured => _client.IsConfigured;

    public string LicensingServerBaseUrl => _client.BaseUrl;

    public bool IsActivated => _serverState.Get().IsLicensed;

    public string MachineHash => _client.MachineHash;

    public LicenseBinding? Binding => _client.Binding;

    /// <summary>
    /// Restores startup state without blindly trusting <c>server.json</c>:
    /// only allows offline startup activation if the licensing server is
    /// configured and a matching, unexpired local binding within the server's
    /// offline grace window exists for this machine.
    /// </summary>
    public void RestoreStartupState(ServerState? initialState, string? configuredLicenseKey = null)
    {
        var key = !string.IsNullOrWhiteSpace(initialState?.LicenseKey)
            ? initialState.LicenseKey.Trim()
            : (!string.IsNullOrWhiteSpace(_client.Binding?.LicenseKey)
                ? _client.Binding.LicenseKey.Trim()
                : (configuredLicenseKey?.Trim() ?? string.Empty));

        var binding = _client.Binding;
        var now = DateTime.UtcNow;
        var hasValidGraceBinding =
            _client.IsConfigured &&
            !string.IsNullOrWhiteSpace(key) &&
            binding is not null &&
            string.Equals(binding.LicenseKey, key, StringComparison.Ordinal) &&
            string.Equals(binding.MachineHash, _client.MachineHash, StringComparison.Ordinal) &&
            now <= binding.ExpiresAt &&
            now <= binding.OfflineGraceUntilUtc;

        _serverState.Update(state =>
        {
            state.LicenseKey = key;
            state.IsLicensed = hasValidGraceBinding;
        });
    }

    /// <summary>
    /// Validates and activates a user-supplied key against NebulaLicensingServer,
    /// persisting the activation state on success.
    /// </summary>
    public async Task<LicenseCheckResult> ActivateAndPersistAsync(string licenseKey, CancellationToken ct = default)
    {
        var normalizedKey = licenseKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return new LicenseCheckResult(false, "ValidationFailed", "License key cannot be empty.", ReachedServer: false);

        if (!_client.IsConfigured)
        {
            _logger.LogWarning("License key rejected: Licensing:BaseUrl is not configured.");
            return LicenseCheckResult.Offline("Licensing:BaseUrl is not configured in appsettings.json.");
        }

        var result = await _client.ActivateOrValidateAsync(normalizedKey, ct);
        _logger.LogInformation(
            "License check for {Key}: valid={Valid}, status={Status}, signature={Verified}, message={Message}",
            Mask(normalizedKey), result.IsValid, result.Status, result.SignatureVerified, result.Message);

        if (result.IsValid)
        {
            _serverState.Update(state =>
            {
                state.IsLicensed = true;
                state.LicenseKey = normalizedKey;
            });

            try
            {
                await _stateFile.SaveAsync(_serverState.Get());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist activated license state.");
            }
        }

        return result;
    }

    public async Task<bool> ValidateLicenseAsync(string licenseKey)
    {
        var result = await ActivateAndPersistAsync(licenseKey);
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
