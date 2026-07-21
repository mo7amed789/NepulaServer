using Microsoft.Extensions.DependencyInjection;
using NebulaServer.Settings;
using System.Runtime.Versioning;

namespace NebulaServer.Services.Licensing;

public static class LicensingServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddMachineFingerprint(this IServiceCollection services)
    {
        services.AddSingleton<IMachineFingerprint, WindowsMachineFingerprint>();
        return services;
    }

    public static IServiceCollection AddLicensing(this IServiceCollection services, bool isProductionEnvironment)
    {
        services.AddSingleton<ILicenseStorage, LicenseStorage>();
        services.AddSingleton<LicenseCrypto>();
        services.AddSingleton<ILicenseManager, LicenseManager>();

        // Use the default .NET certificate validation.
        // This works correctly with ngrok, Cloudflare Tunnel,
        // Let's Encrypt, and other trusted HTTPS endpoints.
        services.AddHttpClient<ILicenseClient, LicenseClient>();

        services.AddHostedService<LicensingHostedService>();
        services.AddHostedService<LicenseValidationHostedService>();
        services.AddSingleton<LicenseShutdownCoordinator>();

        return services;
    }
}