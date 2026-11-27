namespace NebulaServer.Services.Licensing;

/// <summary>
/// Configuration for the link to NebulaLicensingServer, bound from the
/// "Licensing" section of appsettings.json.
/// </summary>
public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>
    /// Base URL of the licensing server, for example https://license.example.com.
    /// Licensing is disabled while this is empty.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// License key shipped with the installation (NBL-XXXX-XXXX-XXXX-XXXX).
    /// A key saved through /api/setup/license takes precedence over this value.
    /// </summary>
    public string LicenseKey { get; set; } = string.Empty;

    /// <summary>
    /// Device name reported to the licensing server and bound to the license.
    /// Defaults to the machine name.
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Version string included in the signed payload.</summary>
    public string NebulaVersion { get; set; } = "1.0.0";

    /// <summary>Minutes between background license validations.</summary>
    public int HeartbeatMinutes { get; set; } = 30;

    /// <summary>Per-request HTTP timeout.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Reject responses whose RSA signature does not verify against the
    /// licensing server public key.
    /// </summary>
    public bool RequireSignature { get; set; } = true;

    /// <summary>
    /// Pinned public key in PEM form. When empty the key is fetched once from
    /// GET /api/client/public-key and cached on disk.
    /// </summary>
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Accept a self-signed TLS certificate from the licensing server. Useful
    /// for on-premise deployments that use the ASP.NET development
    /// certificate; leave false in production.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>Optional fixed machine hash (diagnostics and testing only).</summary>
    public string MachineHashOverride { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
