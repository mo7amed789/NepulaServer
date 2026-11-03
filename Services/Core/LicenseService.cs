using System.Threading.Tasks;

namespace NebulaServer.Services.Core;

public sealed class LicenseService
{
    // In the future, this can be injected with an HttpClient to verify the license against the NebulaLicensingServer
    // or loaded with the public.pem to verify the signature offline.
    
    public Task<bool> ValidateLicenseAsync(string licenseKey)
    {
        // Never treat an arbitrary user-provided string as a production license.
        // Wire this to the licensing service or the bundled public-key verifier
        // before enabling activation in a release build.
        return Task.FromResult(false);
    }
}
