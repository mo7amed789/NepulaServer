using NebulaServer.Models.Pairing;

namespace NebulaServer.Services.Pairing;

public interface IPairingService
{
    Task<PairingInfo> GetPairingInfoAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    Task<string> GetPairingKeyAsync();

    Task<bool> ValidateKeyAsync(string key);

    Task RotateKeyAsync();
}
