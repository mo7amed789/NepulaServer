using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NebulaServer.Settings;

namespace NebulaServer.Services.Licensing;

public sealed class LicensingHostedService : BackgroundService
{
    private readonly ILicenseManager _licenseManager;
    private readonly LicensingOptions _options;
    private readonly ILogger<LicensingHostedService> _logger;

    public LicensingHostedService(
        ILicenseManager licenseManager,
        IOptions<LicensingOptions> options,
        ILogger<LicensingHostedService> logger)
    {
        _licenseManager = licenseManager;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeatInterval = TimeSpan.FromMinutes(
            Math.Max(0.1, _options.HeartbeatIntervalMinutes));

        var validationInterval = TimeSpan.FromHours(
            Math.Max(1, _options.ValidationIntervalHours));

        var nextValidation = DateTime.UtcNow;

        using var timer = new PeriodicTimer(heartbeatInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // إرسال Heartbeat
                await _licenseManager.HeartbeatAsync(stoppingToken);

                // التحقق الدوري من الرخصة
                if (DateTime.UtcNow >= nextValidation)
                {
                    await _licenseManager.ValidateAsync(stoppingToken);

                    nextValidation = DateTime.UtcNow.Add(validationInterval);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Licensing background cycle failed.");
            }
        }
    }
}