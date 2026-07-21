using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaServer.Services.Ngrok;

public sealed class NgrokWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly NgrokManager _ngrok;
    private readonly PublicUrlProvider _provider;
    private readonly ILogger<NgrokWatcher> _logger;

    // Only log "not running" / "recovered" on state transitions instead of on
    // every single poll. NgrokManager.StartAsync now enforces its own attempt
    // throttling/backoff internally, so it's safe (and cheap) to call it on
    // every poll here - it will no-op quickly if a start is already running or
    // a recent attempt already failed.
    private bool _wasRunning = true;

    public NgrokWatcher(
        NgrokManager ngrok,
        PublicUrlProvider provider,
        ILogger<NgrokWatcher> logger)
    {
        _ngrok = ngrok;
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = await _ngrok.GetStatusAsync();

                if (!status.IsRunning)
                {
                    if (_provider.IsReady)
                        _provider.Clear();

                    if (_wasRunning)
                    {
                        _logger.LogWarning("Ngrok is not running. Attempting recovery...");
                        _wasRunning = false;
                    }

                    await _ngrok.StartAsync();
                }
                else
                {
                    if (!_wasRunning)
                    {
                        _logger.LogInformation("Ngrok recovered.");
                        _wasRunning = true;
                    }

                    if (!_provider.IsReady && !string.IsNullOrWhiteSpace(status.PublicUrl))
                        _provider.Set(status.PublicUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ngrok recovery attempt failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}