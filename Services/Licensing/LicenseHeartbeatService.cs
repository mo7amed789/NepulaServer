using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NebulaServer.Services.Core;
using NebulaServer.Services.Licensing;
using NebulaServer.Services.Server;

namespace NebulaServer.Services.Licensing;

/// <summary>
/// Keeps the local license state in step with NebulaLicensingServer: validates
/// on startup and then every <see cref="LicensingOptions.HeartbeatMinutes"/>,
/// honouring the server-issued offline grace window when it cannot be reached.
/// </summary>
public sealed class LicenseHeartbeatService : BackgroundService
{
    private readonly LicenseService _licenseService;
    private readonly LicensingClient _client;
    private readonly LicensingOptions _options;
    private readonly ServerStateService _serverState;
    private readonly ServerStateFileService _stateFile;
    private readonly ILogger<LicenseHeartbeatService> _logger;

    private DateTime _offlineGraceUntilUtc = DateTime.MinValue;

    public LicenseHeartbeatService(
        LicenseService licenseService,
        LicensingClient client,
        IOptions<LicensingOptions> options,
        ServerStateService serverState,
        ServerStateFileService stateFile,
        ILogger<LicenseHeartbeatService> logger)
    {
        _licenseService = licenseService;
        _client = client;
        _options = options.Value;
        _serverState = serverState;
        _stateFile = stateFile;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            Apply(licensed: false, string.Empty);
            _logger.LogInformation("Licensing server link disabled (Licensing:BaseUrl is empty).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_options.HeartbeatMinutes, 1, 24 * 60));

        // Honour a grace window issued before the previous shutdown, so an
        // outage during restart does not drop the license immediately.
        if (_client.Binding?.OfflineGraceUntilUtc is { } persistedGrace && persistedGrace > DateTime.UtcNow)
            _offlineGraceUntilUtc = persistedGrace;
        _logger.LogInformation(
            "Licensing link enabled: {BaseUrl}, machine hash {Hash}, heartbeat every {Minutes} min",
            _options.BaseUrl, LicenseService.Mask(_client.MachineHash), interval.TotalMinutes);

        // First pass right after startup so activation state is authoritative immediately.
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "License heartbeat failed unexpectedly.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var licenseKey = ResolveLicenseKey();
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            Apply(licensed: false, string.Empty);
            _logger.LogDebug("No license key stored yet; skipping license check.");
            return;
        }

        var result = await _licenseService.CheckAsync(licenseKey, ct);

        if (result.IsValid)
        {
            if (result.OfflineGraceUntilUtc is { } grace)
                _offlineGraceUntilUtc = grace;

            Apply(licensed: true, licenseKey);

            var hb = await _licenseService.HeartbeatAsync(licenseKey, ct);
            if (!hb.IsValid)
            {
                _logger.LogWarning("Heartbeat rejected: {Status} {Message}", hb.Status, hb.Message);
                if (hb.ReachedServer)
                    Apply(licensed: false, licenseKey);
            }
            return;
        }

        if (!result.ReachedServer)
        {
            // Network problem, not a rejection: honour the grace window issued
            // with the last successful response instead of revoking the license.
            var graceKnown = _offlineGraceUntilUtc != DateTime.MinValue;
            var inGrace = graceKnown && DateTime.UtcNow <= _offlineGraceUntilUtc;
            var licensed = _serverState.Get().IsLicensed;

            _logger.LogWarning(
                "Licensing server unreachable ({Message}). Offline grace {Grace:u} ({State}); license retained: {Retained}",
                result.Message,
                _offlineGraceUntilUtc,
                graceKnown ? (inGrace ? "active" : "expired") : "not issued",
                licensed || inGrace);

            if (graceKnown && !inGrace)
                Apply(licensed: false, licenseKey);
            else if (inGrace && !licensed)
                Apply(licensed: true, licenseKey);

            return;
        }

        _logger.LogWarning("License {Key} is no longer valid: {Status} - {Message}",
            LicenseService.Mask(licenseKey), result.Status, result.Message);
        Apply(licensed: false, licenseKey);
    }

    private void Apply(bool licensed, string licenseKey)
    {
        var current = _serverState.Get();
        if (current.IsLicensed == licensed && string.Equals(current.LicenseKey, licenseKey, StringComparison.Ordinal))
            return;

        _serverState.Update(state =>
        {
            state.IsLicensed = licensed;
            state.LicenseKey = licenseKey;
        });

        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        try
        {
            await _stateFile.SaveAsync(_serverState.Get());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist license state.");
        }
    }

    private string ResolveLicenseKey()
    {
        var fromState = _serverState.Get().LicenseKey;
        if (!string.IsNullOrWhiteSpace(fromState))
            return fromState;

        var fromBinding = _client.Binding?.LicenseKey;
        if (!string.IsNullOrWhiteSpace(fromBinding))
            return fromBinding;

        return _options.LicenseKey;
    }
}
