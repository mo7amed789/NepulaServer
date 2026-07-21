using NebulaServer.Models;
using NebulaServer.Models.Licensing;

namespace NebulaServer.Services.Licensing
{
    public interface ILicenseManager
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);

        Task<ActivationResult> ActivateAsync(
            string licenseKey,
            CancellationToken cancellationToken = default);

        Task<bool> ValidateAsync(
            CancellationToken cancellationToken = default);

        // التعديل هنا: إرجاع Task<HeartbeatResult> ليتطابق مع الكلاس
        Task<HeartbeatResult> HeartbeatAsync(
            CancellationToken cancellationToken = default);

        bool IsTerminalFailure(LicenseState state);

        LicenseState State { get; }

        LicenseState CurrentState { get; }

        LicenseInfo? CurrentLicense { get; }

        DateTime LastValidationUtc { get; }

        DateTime? LastSuccessfulValidationUtc { get; }

        DateTime? NextValidationUtc { get; }

        TimeSpan OfflineGraceRemaining { get; }

        bool IsOnlineValidated { get; }
    }
}