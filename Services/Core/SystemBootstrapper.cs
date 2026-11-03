using Microsoft.Extensions.Logging;
using NebulaServer.Services.Server;

namespace NebulaServer.Services.Core;

public sealed class SystemBootstrapper
{
    private const int MaxApiAttempts = 60;

    private readonly ILogger<SystemBootstrapper> _logger;
    private readonly int _port;

    public SystemBootstrapper(
        ILogger<SystemBootstrapper> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _port = Math.Clamp(configuration.GetValue("Server:Port", 5000), 1, 65535);
    }

    public async Task InitializeAsync()
    {
        await WaitForApi();
    }

    private async Task WaitForApi()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        for (int i = 0; i < MaxApiAttempts; i++)
        {
            try
            {
                var res = await http.GetAsync($"http://127.0.0.1:{_port}/api/system/ready");

                if (res.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // not ready yet
            }

            await Task.Delay(1000);
        }

        _logger.LogWarning(
            "The local API on port {Port} did not report ready after {Attempts} attempts.",
            _port,
            MaxApiAttempts);
    }
}
