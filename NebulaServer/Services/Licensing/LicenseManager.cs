using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NebulaServer.Models;
using NebulaServer.Models.Licensing;
using NebulaServer.Services;
using NebulaServer.Services.Pairing;
using NebulaServer.Settings;

namespace NebulaServer.Services.Licensing;

public sealed class LicenseManager : ILicenseManager
{
    private readonly ILicenseClient _client;
    private readonly ILicenseStorage _storage;
    private readonly LicenseCrypto _crypto;
    private readonly IMachineFingerprint _fingerprint;
    private readonly IPairingService _pairingService;
    private readonly SystemInfoService _systemInfo;
    private readonly LicensingOptions _options;
    private readonly ILogger<LicenseManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastValidationUtc = DateTime.MinValue;
    private DateTime? _lastSuccessfulValidationUtc;
    private DateTime? _nextValidationUtc;
    private bool _isOnlineValidated;
    private readonly IHostApplicationLifetime _applicationLifetime;

    public LicenseManager(
        ILicenseClient client,
        ILicenseStorage storage,
        LicenseCrypto crypto,
        IMachineFingerprint fingerprint,
        IPairingService pairingService,
        SystemInfoService systemInfo,
        IOptions<LicensingOptions> options,
        ILogger<LicenseManager> logger,
        IHostApplicationLifetime applicationLifetime)
    {
        _client = client;
        _storage = storage;
        _crypto = crypto;
        _fingerprint = fingerprint;
        _pairingService = pairingService;
        _systemInfo = systemInfo;
        _options = options.Value;
        _logger = logger;
        _applicationLifetime = applicationLifetime;
    }

    public LicenseState State { get; private set; } = LicenseState.NotActivated;
    public LicenseState CurrentState => State;
    public LicenseInfo? CurrentLicense { get; private set; }
    public DateTime LastValidationUtc => _lastValidationUtc;
    public DateTime? LastSuccessfulValidationUtc => _lastSuccessfulValidationUtc;
    public DateTime? NextValidationUtc => _nextValidationUtc;
    public TimeSpan OfflineGraceRemaining =>
        CurrentLicense?.OfflineGraceUntilUtc is DateTime graceUntil
            ? graceUntil - DateTime.UtcNow
            : TimeSpan.Zero;
    public bool IsOnlineValidated => _isOnlineValidated;

    private void ShutdownIfLicenseInvalid()
    {
        if (State is LicenseState.Expired
            or LicenseState.Revoked
            or LicenseState.InvalidSignature
            or LicenseState.ValidationFailed
            or LicenseState.TamperedLicense
            or LicenseState.MachineMismatch)
        {
            _logger.LogCritical(
                "License transitioned to {State}. Core functionality is now restricted.",
                State);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing licensing...");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LicenseInfo? local;
            try
            {
                local = await _storage.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to load local license from storage.");
                CurrentLicense = null;
                State = LicenseState.NotActivated;
                return;
            }

            if (local is null)
            {
                _logger.LogInformation("No local license found.");
                CurrentLicense = null;
                State = LicenseState.NotActivated;
                _lastValidationUtc = DateTime.MinValue;
                _lastSuccessfulValidationUtc = null;
                _nextValidationUtc = null;
                _isOnlineValidated = false;
                return;
            }

            if (!_crypto.Verify(local))
            {
                _logger.LogWarning("Local license signature verification failed.");
                CurrentLicense = null;
                State = LicenseState.ValidationFailed;
                _lastValidationUtc = DateTime.UtcNow;
                _nextValidationUtc = null;
                _isOnlineValidated = false;
                return;
            }

            CurrentLicense = local;
            State = EvaluateLocalState(local);
            _lastValidationUtc = local.LastValidationUtc == default ? DateTime.UtcNow : local.LastValidationUtc;
            _lastSuccessfulValidationUtc = local.LastSuccessfulValidationUtc ?? (local.LastValidationUtc == default ? null : local.LastValidationUtc);
            _nextValidationUtc = _lastValidationUtc == DateTime.MinValue
                ? null
                : _lastValidationUtc.AddHours(Math.Max(1, _options.ValidationIntervalHours));
            _isOnlineValidated = State == LicenseState.Valid;
            _logger.LogInformation("License loaded with state {State}.", State);
        }
        finally
        {
            _gate.Release();
        }

        if (CurrentLicense is null) return;

        if (!await _client.IsServerReachableAsync(cancellationToken))
        {
            _logger.LogWarning("Licensing server is unreachable. Local license state: {State}.", State);
            _isOnlineValidated = false;
            return;
        }

        await ValidateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActivationResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            _logger.LogWarning("Activation attempted with an empty license key.");
            return new ActivationResult { Success = false, Message = "License key is required.", LicenseKey = string.Empty };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActivationResult result;
            var machineHash = _fingerprint.GetMachineHash();
            ClientActivateRequest request;
            try
            {
                request = await BuildActivateRequestAsync(licenseKey, machineHash, cancellationToken).ConfigureAwait(false);

                await _storage.DeleteAsync(cancellationToken);
                CurrentLicense = null;
                State = LicenseState.NotActivated;
                _lastValidationUtc = DateTime.MinValue;
                _lastSuccessfulValidationUtc = null;
                _nextValidationUtc = null;
                _isOnlineValidated = false;

                result = await _client.ActivateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Activation request failed.");
                return new ActivationResult { Success = false, Message = "Activation request failed. Please try again.", LicenseKey = licenseKey };
            }

            if (!result.Success)
            {
                _logger.LogWarning("License activation was rejected by the server.");
                State = MapLicenseState(result.Status);
                return result;
            }

            var licenseInfo = new LicenseInfo
            {
                LicenseKey = result.LicenseKey,
                MachineHash = machineHash,
                DeviceName = request.DeviceName,
                NebulaVersion = request.NebulaVersion,
                ServerId = request.ServerId,
                ExpiresAt = result.ExpiresAt,
                SignedAtUtc = result.SignedAtUtc,
                OfflineGraceUntilUtc = result.OfflineGraceUntilUtc,
                SignatureAlgorithm = result.SignatureAlgorithm,
                Signature = result.Signature,
                Activated = true
            };

            if (!_crypto.Verify(licenseInfo))
            {
                _logger.LogWarning("Activation succeeded but the returned license signature is invalid.");
                return new ActivationResult
                {
                    Success = false,
                    Message = "Activation response signature verification failed.",
                    LicenseKey = result.LicenseKey,
                    ExpiresAt = result.ExpiresAt
                };
            }

            licenseInfo.LastValidationUtc = DateTime.UtcNow;
            licenseInfo.LastSuccessfulValidationUtc = DateTime.UtcNow;

            if (!await PersistLicenseAsync(licenseInfo, cancellationToken).ConfigureAwait(false))
            {
                return new ActivationResult { Success = false, Message = "Activation succeeded but local license storage failed.", LicenseKey = result.LicenseKey, ExpiresAt = result.ExpiresAt };
            }

            CurrentLicense = licenseInfo;
            State = LicenseState.Valid;
            _lastValidationUtc = DateTime.UtcNow;
            _lastSuccessfulValidationUtc = _lastValidationUtc;
            _nextValidationUtc = _lastValidationUtc.AddHours(Math.Max(1, _options.ValidationIntervalHours));
            _isOnlineValidated = true;
            _logger.LogInformation("License activated.");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentLicense is null)
            {
                State = LicenseState.NotActivated;
                return false;
            }

            if (!await _client.IsServerReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Licensing server is unreachable. Validating locally based on Expiration Date.");
                var now = DateTime.UtcNow;

                if (CurrentLicense.ExpiresAt > now)
                {
                    State = LicenseState.Valid;
                }
                else
                {
                    State = LicenseState.Expired;
                    ShutdownIfLicenseInvalid();
                }

                _isOnlineValidated = false;
                _lastValidationUtc = DateTime.UtcNow;
                _nextValidationUtc = _lastValidationUtc.AddHours(Math.Max(1, _options.ValidationIntervalHours));
                return false;
            }

            _lastValidationUtc = DateTime.UtcNow;
            _nextValidationUtc = _lastValidationUtc.AddHours(Math.Max(1, _options.ValidationIntervalHours));

            ValidationResult response;
            try
            {
                response = await _client.ValidateAsync(
                    await BuildValidateRequestAsync(CurrentLicense, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Licensing server unreachable. Using local license.");
                if (CurrentLicense!.ExpiresAt > DateTime.UtcNow)
                {
                    State = LicenseState.Valid;
                }
                else
                {
                    State = LicenseState.Expired;
                    ShutdownIfLicenseInvalid();
                }
                _isOnlineValidated = false;
                return false;
            }

            return await ApplyValidationResultAsync(response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // تم تنظيف وتصحيح الدالة وإرجاع نوع HeartbeatResult بشكل سليم
    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentLicense is null || State is LicenseState.NotActivated or LicenseState.ValidationFailed)
        {
            return new HeartbeatResult
            {
                Success = false,
                Message = "No active or valid license to heartbeat."
            };
        }

        try
        {
            var request = await BuildHeartbeatRequestAsync(CurrentLicense, cancellationToken).ConfigureAwait(false);

            // نعتمد على الكلاينت هنا لعمل الاتصال بالشبكة
            var result = await _client.HeartbeatAsync(request, cancellationToken).ConfigureAwait(false);

            if (!result.Success && result.ErrorCode == "LIC-002")
            {
                _logger.LogWarning("License has been revoked by the licensing server.");

                await _storage.DeleteAsync(cancellationToken).ConfigureAwait(false);

                CurrentLicense = null;
                State = LicenseState.Revoked; // تحديث الحالة لتكون Revoked

                _lastValidationUtc = DateTime.MinValue;
                _lastSuccessfulValidationUtc = null;
                _nextValidationUtc = null;
                _isOnlineValidated = false;

                ShutdownIfLicenseInvalid(); // تقييد النظام لكون الرخصة سُحبت

                return result;
            }

            _logger.LogInformation("Heartbeat sent successfully.");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat failed due to a transient network error.");

            if (CurrentLicense is not null)
            {
                State = EvaluateLocalState(CurrentLicense);
                ShutdownIfLicenseInvalid();
            }

            return new HeartbeatResult
            {
                Success = false,
                Message = "Network error during heartbeat."
            };
        }
    }

    private async Task<bool> ApplyValidationResultAsync(ValidationResult response, CancellationToken cancellationToken)
    {
        if (response.IsValid)
        {
            var updatedLicense = new LicenseInfo
            {
                LicenseKey = CurrentLicense!.LicenseKey,
                MachineHash = _fingerprint.GetMachineHash(),
                DeviceName = string.IsNullOrWhiteSpace(CurrentLicense.DeviceName) ? GetDeviceName() : CurrentLicense.DeviceName,
                NebulaVersion = string.IsNullOrWhiteSpace(CurrentLicense.NebulaVersion) ? _systemInfo.Version : CurrentLicense.NebulaVersion,
                ServerId = string.IsNullOrWhiteSpace(CurrentLicense.ServerId) ? (await _pairingService.GetPairingInfoAsync().ConfigureAwait(false)).ServerId.ToString() : CurrentLicense.ServerId,
                ExpiresAt = response.ExpiresAt,
                SignedAtUtc = response.SignedAtUtc ?? CurrentLicense.SignedAtUtc,
                OfflineGraceUntilUtc = response.OfflineGraceUntilUtc ?? CurrentLicense.OfflineGraceUntilUtc,
                SignatureAlgorithm = response.SignatureAlgorithm,
                Signature = response.Signature,
                Activated = true
            };

            if (!_crypto.Verify(updatedLicense))
            {
                _logger.LogWarning("Validation succeeded but the returned license signature is invalid.");
                State = LicenseState.InvalidSignature;
                _isOnlineValidated = false;
                return false;
            }

            updatedLicense.LastValidationUtc = DateTime.UtcNow;
            updatedLicense.LastSuccessfulValidationUtc = DateTime.UtcNow;

            CurrentLicense = updatedLicense;

            if (!await PersistLicenseAsync(CurrentLicense, cancellationToken).ConfigureAwait(false))
            {
                State = LicenseState.ValidationFailed;
                _isOnlineValidated = false;
                return false;
            }

            State = LicenseState.Valid;
            _lastSuccessfulValidationUtc = DateTime.UtcNow;
            _isOnlineValidated = true;
            _logger.LogInformation("License validation succeeded.");
            return true;
        }

        State = MapLicenseState(response.Status);
        _isOnlineValidated = false;
        if (State is LicenseState.NetworkFailure or LicenseState.Timeout or LicenseState.ServerUnavailable)
        {
            var offlineState = ApplyOfflineGrace();
            if (offlineState == LicenseState.OfflineGrace)
            {
                State = offlineState;
                return false;
            }
        }

        switch (State)
        {
            case LicenseState.Expired: _logger.LogWarning("License expired."); break;
            case LicenseState.Revoked: _logger.LogWarning("License revoked."); break;
            case LicenseState.MachineMismatch: _logger.LogWarning("License machine mismatch."); break;
            case LicenseState.InvalidSignature: _logger.LogWarning("License signature invalid."); break;
            case LicenseState.TamperedLicense: _logger.LogWarning("License tampering detected."); break;
            default: _logger.LogWarning("License validation failed. Status={Status}. Message={Message}", response.Status, response.Message); break;
        }

        return false;
    }

    private LicenseState EvaluateLocalState(LicenseInfo license)
    {
        if (license.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogWarning("Local license has expired.");
            return LicenseState.Expired;
        }
        return LicenseState.Valid;
    }

    private LicenseState ApplyOfflineGrace()
    {
        if (CurrentLicense is null) return LicenseState.NotActivated;
        var now = DateTime.UtcNow;

        if (CurrentLicense.ExpiresAt <= DateTime.UtcNow) return EvaluateExpiredState(CurrentLicense, now);

        if (CurrentLicense.OfflineGraceUntilUtc is not null && CurrentLicense.OfflineGraceUntilUtc > now)
        {
            _logger.LogWarning("Offline grace active for license.");
            return LicenseState.OfflineGrace;
        }

        _logger.LogWarning("License validation failed.");
        return LicenseState.ValidationFailed;
    }

    private LicenseState EvaluateExpiredState(LicenseInfo license, DateTime now)
    {
        if (license.OfflineGraceUntilUtc is not null && license.OfflineGraceUntilUtc > now)
        {
            _logger.LogWarning("Offline signed grace is active for an expired license.");
            return LicenseState.OfflineGrace;
        }
        _logger.LogWarning("License expired.");
        return LicenseState.Expired;
    }

    private static LicenseState MapLicenseState(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return LicenseState.ValidationFailed;
        if (status.Equals(nameof(LicenseState.Expired), StringComparison.OrdinalIgnoreCase)) return LicenseState.Expired;
        if (status.Equals(nameof(LicenseState.Revoked), StringComparison.OrdinalIgnoreCase)) return LicenseState.Revoked;
        if (status.Equals("MachineMismatch", StringComparison.OrdinalIgnoreCase)) return LicenseState.MachineMismatch;
        if (status.Equals("NetworkFailure", StringComparison.OrdinalIgnoreCase)) return LicenseState.NetworkFailure;
        if (status.Equals("Timeout", StringComparison.OrdinalIgnoreCase)) return LicenseState.Timeout;
        if (status.Equals("ServerUnavailable", StringComparison.OrdinalIgnoreCase)) return LicenseState.ServerUnavailable;
        if (status.Equals("InvalidSignature", StringComparison.OrdinalIgnoreCase)) return LicenseState.InvalidSignature;
        if (status.Equals("TamperedLicense", StringComparison.OrdinalIgnoreCase)) return LicenseState.TamperedLicense;

        return LicenseState.ValidationFailed;
    }

    private async Task<bool> PersistLicenseAsync(LicenseInfo license, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.SaveAsync(license, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License validation failed due to a network error or invalid server response. Validating locally based on Expiration Date.");
            var now = DateTime.UtcNow;

            if (license.ExpiresAt > now)
            {
                State = LicenseState.Valid;
                _logger.LogInformation("Offline fallback applied successfully. Server remains active until {ExpiresAt}.", license.ExpiresAt);
            }
            else
            {
                State = LicenseState.Expired;
                ShutdownIfLicenseInvalid();
            }

            _isOnlineValidated = false;
            _lastValidationUtc = DateTime.UtcNow;
            _nextValidationUtc = _lastValidationUtc.AddHours(Math.Max(1, _options.ValidationIntervalHours));
            return false;
        }
    }

    private async Task<ClientActivateRequest> BuildActivateRequestAsync(
        string licenseKey, string machineHash, CancellationToken cancellationToken)
    {
        var pairing = await _pairingService.GetPairingInfoAsync().ConfigureAwait(false);
        return new ClientActivateRequest
        {
            LicenseKey = licenseKey,
            MachineHash = machineHash,
            DeviceName = GetDeviceName(),
            NebulaVersion = _systemInfo.Version,
            ServerId = pairing.ServerId.ToString()
        };
    }

    private async Task<ClientValidateRequest> BuildValidateRequestAsync(
        LicenseInfo license, CancellationToken cancellationToken)
    {
        var pairing = await _pairingService.GetPairingInfoAsync().ConfigureAwait(false);
        return new ClientValidateRequest
        {
            LicenseKey = license.LicenseKey,
            MachineHash = _fingerprint.GetMachineHash(),
            DeviceName = GetDeviceName(),
            NebulaVersion = _systemInfo.Version,
            ServerId = pairing.ServerId.ToString()
        };
    }

    public bool IsTerminalFailure(LicenseState state) =>
        state is LicenseState.Revoked or LicenseState.Expired or LicenseState.MachineMismatch
        or LicenseState.ValidationFailed or LicenseState.InvalidSignature or LicenseState.TamperedLicense;

    private async Task<ClientHeartbeatRequest> BuildHeartbeatRequestAsync(
        LicenseInfo license, CancellationToken cancellationToken)
    {
        var pairing = await _pairingService.GetPairingInfoAsync().ConfigureAwait(false);
        return new ClientHeartbeatRequest
        {
            LicenseKey = license.LicenseKey,
            MachineHash = _fingerprint.GetMachineHash(),
            DeviceName = string.IsNullOrWhiteSpace(license.DeviceName) ? GetDeviceName() : license.DeviceName,
            NebulaVersion = string.IsNullOrWhiteSpace(license.NebulaVersion) ? _systemInfo.Version : license.NebulaVersion,
            ServerId = string.IsNullOrWhiteSpace(license.ServerId) ? pairing.ServerId.ToString() : license.ServerId,
            CurrentTimeUtc = DateTime.UtcNow
        };
    }

    private static string GetDeviceName() => Environment.MachineName;
}