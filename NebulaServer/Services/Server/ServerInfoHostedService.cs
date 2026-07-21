using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaServer.Services.Ngrok;

namespace NebulaServer.Services.Server;

public sealed class ServerInfoHostedService : BackgroundService
{
    private readonly SystemInfoService _systemInfo;
    private readonly ServerStateService _serverState;
    private readonly ILogger<ServerInfoHostedService> _logger;

    public ServerInfoHostedService(
        SystemInfoService systemInfo,
        ServerStateService serverState,
        ILogger<ServerInfoHostedService> logger)
    {
        _systemInfo = systemInfo;
        _serverState = serverState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========== Server Information Service ==========");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _serverState.Update(state =>
                {
                    state.DeviceId = _systemInfo.DeviceId;
                    state.LocalIp = _systemInfo.GetLocalIpAddress();
                    state.Port = _systemInfo.Port;
                    state.IsServerRunning = true;
                    state.Version = _systemInfo.Version;
                });

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Server Information Service stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server Information Service crashed.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _serverState.Update(state =>
        {
            state.IsServerRunning = false;
        });

        await base.StopAsync(cancellationToken);
    }
}