using NebulaServer.Models;
using NebulaServer.Models.Licensing;
using ValidationResult = NebulaServer.Models.Licensing.ValidationResult;

namespace NebulaServer.Services.Licensing;

public interface ILicenseClient
{
    Task<bool> IsServerReachableAsync(CancellationToken cancellationToken = default);

    Task<ActivationResult> ActivateAsync(
        ClientActivateRequest request,
        CancellationToken cancellationToken = default);

    Task<ValidationResult> ValidateAsync(
        ClientValidateRequest request,
        CancellationToken cancellationToken = default);

    Task<HeartbeatResult> HeartbeatAsync(ClientHeartbeatRequest request, CancellationToken cancellationToken = default);
}
