using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaServer.Services.Ngrok;

public class NgrokHostedService : BackgroundService
{
    private readonly INgrokManager _ngrokManager;
    private readonly ILogger<NgrokHostedService> _logger;

    public NgrokHostedService(
        INgrokManager ngrokManager,
        ILogger<NgrokHostedService> logger)
    {
        _ngrokManager = ngrokManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Ngrok Hosted Service...");

        try
        {
            await _ngrokManager.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ngrok could not be started. Nebula Server will continue in Local Mode.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("Ngrok Hosted Service stopped.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping ngrok...");

        await _ngrokManager.StopAsync();

        await base.StopAsync(cancellationToken);
    }
}
