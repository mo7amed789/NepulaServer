using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Licensing;

namespace NebulaServer.Services.Licensing;

public sealed class LicenseValidationHostedService : BackgroundService
{
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromMinutes(15);
    private readonly ILicenseManager _licenseManager;
    private readonly LicenseShutdownCoordinator _shutdownCoordinator;
    private readonly ILogger<LicenseValidationHostedService> _logger;

    public LicenseValidationHostedService(
        ILicenseManager licenseManager,
        LicenseShutdownCoordinator shutdownCoordinator,
        ILogger<LicenseValidationHostedService> logger)
    {
        _licenseManager = licenseManager;
        _shutdownCoordinator = shutdownCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ValidationInterval, stoppingToken).ConfigureAwait(false);
                var valid = await _licenseManager.ValidateAsync(stoppingToken).ConfigureAwait(false);

                if (valid)
                {
                    continue;
                }

                var state = _licenseManager.CurrentState;
                if (_shutdownCoordinator.ShouldTerminate(state))
                {
                    _logger.LogCritical("License validation failed with terminal state {State}. Initiating shutdown.", state);
                    await _shutdownCoordinator.TerminateAsync(state, stoppingToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "License validation cycle failed.");
            }
        }
    }
}
