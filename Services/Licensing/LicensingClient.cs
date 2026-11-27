using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NebulaServer.Helpers;
using NebulaServer.Services;

namespace NebulaServer.Services.Licensing;

/// <summary>
/// HTTP client for NebulaLicensingServer (<c>/api/client/activate</c>,
/// <c>/api/client/validate</c>, <c>/api/client/heartbeat</c> and
/// <c>/api/client/public-key</c>) with offline RSA-SHA256 verification of the
/// signed responses.
/// </summary>
public sealed class LicensingClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicensingOptions _options;
    private readonly ILogger<LicensingClient> _logger;
    private readonly SystemInfoService _systemInfo;
    private readonly HttpClient _http;
    private readonly Lazy<string> _machineHash;

    private string? _publicKeyPem;
    private LicenseBinding? _binding;

    public LicensingClient(
        IOptions<LicensingOptions> options,
        ILogger<LicensingClient> logger,
        SystemInfoService systemInfo)
    {
        _options = options.Value;
        _logger = logger;
        _systemInfo = systemInfo;
        _machineHash = new Lazy<string>(ResolveMachineHash);

        var handler = new HttpClientHandler();
        if (_options.AllowInvalidCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler)
        {
            BaseAddress = Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri)
                ? baseUri
                : new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 3, 120))
        };
    }

    public bool IsConfigured => _options.IsConfigured;

    public string MachineHash => _machineHash.Value;

    public string ServerId => _systemInfo.DeviceId;

    public string DeviceName => string.IsNullOrWhiteSpace(_options.DeviceName)
        ? Environment.MachineName
        : _options.DeviceName;

    /// <summary>Activation data persisted locally, if any.</summary>
    public LicenseBinding? Binding => _binding ??= LicenseBindingStore.Load();

    public async Task<LicenseCheckResult> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return LicenseCheckResult.Offline("Licensing server URL is not configured.");

        var request = new ClientActivateRequest
        {
            LicenseKey = licenseKey,
            MachineHash = MachineHash,
            DeviceName = DeviceName,
            NebulaVersion = _options.NebulaVersion,
            ServerId = ServerId
        };

        var (envelope, error, reached) = await PostAsync<ClientActivationResponse>("api/client/activate", request, ct);
        var data = envelope?.Data;

        if (data is null)
        {
            var message = error ?? envelope?.Message ?? "Activation failed.";
            if (reached)
                _logger.LogWarning("License activation rejected: {Message} ({Code})", message, envelope?.ErrorCode);
            return new LicenseCheckResult(false, reached ? "Rejected" : "Offline", message,
                ReachedServer: reached, ErrorCode: envelope?.ErrorCode);
        }

        // The activation response is signed over the values we sent plus the
        // server's dates, so it can be verified without any local state.
        var verified = VerifySignatureWithDateVariants(
            data.LicenseKey, MachineHash, DeviceName, _options.NebulaVersion, ServerId,
            data.ExpiresAt, data.SignedAtUtc, data.OfflineGraceUntilUtc, activated: true, data.Signature);

        if (envelope is null || !data.Success)
        {
            var message = envelope?.Message ?? data.Message;
            _logger.LogWarning("License activation refused: {Message}", message);
            return new LicenseCheckResult(false, data.Status, message, ReachedServer: true, ErrorCode: envelope?.ErrorCode);
        }

        if (_options.RequireSignature && !verified)
        {
            _logger.LogError("License activation signature could not be verified against the licensing server public key.");
            return new LicenseCheckResult(false, "InvalidSignature",
                "Activation response failed signature verification.", ReachedServer: true);
        }

        _binding = new LicenseBinding
        {
            LicenseKey = data.LicenseKey,
            MachineHash = MachineHash,
            DeviceName = DeviceName,
            ServerId = ServerId,
            ActivatedAtUtc = DateTime.UtcNow,
            ExpiresAt = data.ExpiresAt,
            OfflineGraceUntilUtc = data.OfflineGraceUntilUtc
        };
        LicenseBindingStore.Save(_binding);

        _logger.LogInformation(
            "License {Key} activated for {Device}; expires {Expires:u}, signature verified: {Verified}",
            data.LicenseKey, DeviceName, data.ExpiresAt, verified);

        return new LicenseCheckResult(true, data.Status, "License activated.", data.ExpiresAt,
            data.OfflineGraceUntilUtc, verified);
    }

    public async Task<LicenseCheckResult> ValidateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return LicenseCheckResult.Offline("Licensing server URL is not configured.");

        var request = new ClientValidateRequest
        {
            LicenseKey = licenseKey,
            MachineHash = MachineHash,
            NebulaVersion = _options.NebulaVersion,
            ServerId = ServerId
        };

        var (envelope, error, reached) = await PostAsync<ClientValidationResponse>("api/client/validate", request, ct);
        var data = envelope?.Data;

        if (data is null)
        {
            var message = error ?? envelope?.Message ?? "Validation failed.";
            return new LicenseCheckResult(false, reached ? "Rejected" : "Offline", message,
                ReachedServer: reached, ErrorCode: envelope?.ErrorCode);
        }

        if (!data.IsValid)
            return new LicenseCheckResult(false, data.Status, data.Message, data.ExpiresAt,
                data.OfflineGraceUntilUtc, ReachedServer: true, ErrorCode: envelope?.ErrorCode);

        // Validate signs the device name stored on the server, which the
        // response does not echo; it is read back from the activation binding.
        var deviceName = Binding?.DeviceName;
        if (string.IsNullOrEmpty(deviceName))
            deviceName = DeviceName;

        var verified = VerifySignatureWithDateVariants(
            licenseKey, MachineHash, deviceName, _options.NebulaVersion, ServerId,
            data.ExpiresAt, data.SignedAtUtc, data.OfflineGraceUntilUtc, activated: true, data.Signature);

        if (_options.RequireSignature && !verified)
        {
            // A stored device name is required to verify: report, do not brick.
            _logger.LogWarning(
                "License validation signature could not be verified (device name '{Device}' from local binding). " +
                "Activate again to refresh the binding.", deviceName);
            return new LicenseCheckResult(false, "UnverifiedSignature",
                "Validation response signature did not verify.", data.ExpiresAt, data.OfflineGraceUntilUtc,
                ReachedServer: true);
        }

        _logger.LogInformation("License {Key} valid; expires {Expires:u}; {Days} day(s) remaining",
            licenseKey, data.ExpiresAt, data.DaysRemaining);

        return new LicenseCheckResult(true, data.Status, data.Message, data.ExpiresAt,
            data.OfflineGraceUntilUtc, verified);
    }

    public async Task<LicenseCheckResult> HeartbeatAsync(string licenseKey, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return LicenseCheckResult.Offline("Licensing server URL is not configured.");

        var request = new ClientHeartbeatRequest
        {
            LicenseKey = licenseKey,
            MachineHash = MachineHash,
            NebulaVersion = _options.NebulaVersion,
            ServerId = ServerId,
            CurrentTimeUtc = DateTime.UtcNow
        };

        var (envelope, error, reached) = await PostAsync<ClientHeartbeatResponse>("api/client/heartbeat", request, ct);
        if (envelope?.Data is null)
            return new LicenseCheckResult(false, reached ? "Rejected" : "Offline",
                error ?? envelope?.Message ?? "Heartbeat failed.",
                ReachedServer: reached, ErrorCode: envelope?.ErrorCode);

        return new LicenseCheckResult(true, envelope.Data.Status, "Heartbeat accepted.", ReachedServer: true);
    }

    /// <summary>
    /// Activate, then validate. Activation is tried first because a key that has
    /// never been bound to this machine cannot be validated.
    /// </summary>
    public async Task<LicenseCheckResult> ActivateOrValidateAsync(string licenseKey, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(licenseKey, ct);
        if (validation.IsValid)
            return validation;

        if (validation.ReachedServer && validation.Status is "UnverifiedSignature")
            return validation;

        var activation = await ActivateAsync(licenseKey, ct);
        if (activation.IsValid)
            return activation;

        // Already bound to this machine: report the original validation answer.
        return validation.ReachedServer ? validation : activation;
    }

    private async Task<(ApiEnvelope<T>? Envelope, string? Error, bool ReachedServer)> PostAsync<T>(
        string path, object request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, request, JsonOptions, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (string.IsNullOrWhiteSpace(body))
                return (null, $"Licensing server returned HTTP {(int)response.StatusCode} with an empty body.", true);

            var envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(body, JsonOptions);
            if (envelope is null)
                return (null, "Licensing server returned an unreadable response.", true);

            return (envelope, null, true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Licensing request to {Path} timed out.", path);
            return (null, "Licensing server request timed out.", false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Licensing server unreachable at {Path}: {Message}", path, ex.Message);
            return (null, $"Licensing server unreachable: {ex.Message}", false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Licensing response from {Path} could not be parsed.", path);
            return (null, "Licensing server returned malformed JSON.", true);
        }
    }

    private string ResolveMachineHash() =>
        string.IsNullOrWhiteSpace(_options.MachineHashOverride)
            ? MachineFingerprint.Compute()
            : _options.MachineHashOverride;

    // =========================================================================
    // Signature verification
    // =========================================================================

    /// <summary>
    /// Mirrors LicenseSigningService.Sign in NebulaLicensingServer:
    /// licenseKey|machineHash|deviceName|nebulaVersion|serverId|expiresAt|signedAt|graceUntil|activated
    /// with every DateTime rendered as round-trip ("O") UTC.
    /// </summary>
    private static string BuildPayload(
        string licenseKey, string machineHash, string deviceName, string nebulaVersion,
        string serverId, DateTime expiresAt, DateTime signedAt, DateTime graceUntil, bool activated,
        int dateVariant = 0)
    {
        return string.Join("|",
            licenseKey,
            machineHash,
            deviceName,
            nebulaVersion,
            serverId,
            RenderDate(expiresAt, (dateVariant & 1) != 0),
            RenderDate(signedAt, (dateVariant & 2) != 0),
            RenderDate(graceUntil, (dateVariant & 4) != 0),
            activated.ToString());
    }

    /// <summary>
    /// SQL Server round-trips store DateTime without a Kind, and the licensing
    /// server then signs <c>value.ToUniversalTime()</c>. Both the "already UTC"
    /// and "local wall clock" readings are attempted so the check survives that
    /// ambiguity instead of rejecting valid responses.
    /// </summary>
    private static string RenderDate(DateTime value, bool treatUnspecifiedAsLocal)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => treatUnspecifiedAsLocal ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private bool VerifySignature(string payload, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64))
            return false;

        var pem = GetPublicKeyPem();
        if (string.IsNullOrWhiteSpace(pem))
            return false;

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var signature = Convert.FromBase64String(signatureBase64);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(payload), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "License signature verification failed.");
            return false;
        }
        catch (FormatException)
        {
            _logger.LogWarning("License signature was not valid base64.");
            return false;
        }
    }

    /// <summary>
    /// Verifies a signed response, trying the DateTime reading variants the
    /// licensing server may have used.
    /// </summary>
    private bool VerifySignatureWithDateVariants(
        string licenseKey, string machineHash, string deviceName, string nebulaVersion, string serverId,
        DateTime expiresAt, DateTime signedAt, DateTime graceUntil, bool activated, string signatureBase64)
    {
        for (var variant = 0; variant < 8; variant++)
        {
            var payload = BuildPayload(licenseKey, machineHash, deviceName, nebulaVersion, serverId,
                expiresAt, signedAt, graceUntil, activated, variant);
            if (VerifySignature(payload, signatureBase64))
                return true;
        }

        return false;
    }

    private string? GetPublicKeyPem()
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicKeyPem))
            return _options.PublicKeyPem.Trim();

        if (_publicKeyPem is not null)
            return _publicKeyPem;

        var cached = Path.Combine(RuntimePaths.Data, "license-public-key.pem");
        if (File.Exists(cached))
        {
            _publicKeyPem = File.ReadAllText(cached).Trim();
            return _publicKeyPem;
        }

        try
        {
            var pem = _http.GetStringAsync("api/client/public-key").GetAwaiter().GetResult().Trim();
            if (pem.Contains("BEGIN", StringComparison.Ordinal))
            {
                File.WriteAllText(cached, pem);
                _publicKeyPem = pem;
                return pem;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch the licensing server public key.");
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Small JSON store for the activation binding.</summary>
internal static class LicenseBindingStore
{
    private static string Path_ => System.IO.Path.Combine(RuntimePaths.Data, "license-binding.json");

    public static LicenseBinding? Load()
    {
        try
        {
            if (!File.Exists(Path_))
                return null;

            return JsonSerializer.Deserialize<LicenseBinding>(File.ReadAllText(Path_));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(LicenseBinding binding)
    {
        try
        {
            Directory.CreateDirectory(RuntimePaths.Data);
            File.WriteAllText(Path_,
                JsonSerializer.Serialize(binding, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Persisting the binding is best effort.
        }
    }
}
