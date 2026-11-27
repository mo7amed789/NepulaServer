using System.Text.Json.Serialization;

namespace NebulaServer.Services.Licensing;

// =============================================================================
// Wire contracts for NebulaLicensingServer (POST /api/client/*).
// Field names match DTOs/Client/Requests and DTOs/Client/Responses there.
// =============================================================================

public sealed class ClientActivateRequest
{
    [JsonPropertyName("licenseKey")] public string LicenseKey { get; init; } = string.Empty;
    [JsonPropertyName("machineHash")] public string MachineHash { get; init; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("nebulaVersion")] public string NebulaVersion { get; init; } = string.Empty;
    [JsonPropertyName("serverId")] public string ServerId { get; init; } = string.Empty;
}

public sealed class ClientValidateRequest
{
    [JsonPropertyName("licenseKey")] public string LicenseKey { get; init; } = string.Empty;
    [JsonPropertyName("machineHash")] public string MachineHash { get; init; } = string.Empty;
    [JsonPropertyName("nebulaVersion")] public string NebulaVersion { get; init; } = string.Empty;
    [JsonPropertyName("serverId")] public string ServerId { get; init; } = string.Empty;
}

public sealed class ClientHeartbeatRequest
{
    [JsonPropertyName("licenseKey")] public string LicenseKey { get; init; } = string.Empty;
    [JsonPropertyName("machineHash")] public string MachineHash { get; init; } = string.Empty;
    [JsonPropertyName("nebulaVersion")] public string NebulaVersion { get; init; } = string.Empty;
    [JsonPropertyName("serverId")] public string ServerId { get; init; } = string.Empty;
    [JsonPropertyName("currentTimeUtc")] public DateTime CurrentTimeUtc { get; init; }
}

public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; }
    [JsonPropertyName("data")] public T? Data { get; init; }
}

public sealed class ClientActivationResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; init; }
    [JsonPropertyName("licenseKey")] public string LicenseKey { get; init; } = string.Empty;
    [JsonPropertyName("machineName")] public string MachineName { get; init; } = string.Empty;
    [JsonPropertyName("signature")] public string Signature { get; init; } = string.Empty;
    [JsonPropertyName("signatureAlgorithm")] public string SignatureAlgorithm { get; init; } = "RSA-SHA256";
    [JsonPropertyName("signedAtUtc")] public DateTime SignedAtUtc { get; init; }
    [JsonPropertyName("offlineGraceUntilUtc")] public DateTime OfflineGraceUntilUtc { get; init; }
}

public sealed class ClientValidationResponse
{
    [JsonPropertyName("isValid")] public bool IsValid { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; init; }
    [JsonPropertyName("daysRemaining")] public int DaysRemaining { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("machineName")] public string MachineName { get; init; } = string.Empty;
    [JsonPropertyName("signature")] public string Signature { get; init; } = string.Empty;
    [JsonPropertyName("signatureAlgorithm")] public string SignatureAlgorithm { get; init; } = "RSA-SHA256";
    [JsonPropertyName("signedAtUtc")] public DateTime SignedAtUtc { get; init; }
    [JsonPropertyName("offlineGraceUntilUtc")] public DateTime OfflineGraceUntilUtc { get; init; }
}

public sealed class ClientHeartbeatResponse
{
    [JsonPropertyName("currentServerTimeUtc")] public DateTime CurrentServerTimeUtc { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
}

/// <summary>Locally persisted outcome of a successful activation, needed to
/// verify validation signatures (the server signs the stored device name).</summary>
public sealed class LicenseBinding
{
    public string LicenseKey { get; set; } = string.Empty;
    public string MachineHash { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public DateTime ActivatedAtUtc { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime OfflineGraceUntilUtc { get; set; }
}

/// <summary>Result of a licensing round trip, independent of HTTP details.</summary>
public sealed record LicenseCheckResult(
    bool IsValid,
    string Status,
    string Message,
    DateTime? ExpiresAt = null,
    DateTime? OfflineGraceUntilUtc = null,
    bool SignatureVerified = false,
    bool ReachedServer = true,
    string? ErrorCode = null)
{
    public static LicenseCheckResult Offline(string message) =>
        new(false, "Offline", message, ReachedServer: false);
}
