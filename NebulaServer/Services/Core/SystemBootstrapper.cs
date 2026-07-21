using Microsoft.Extensions.Logging;
using NebulaServer.Services.Ngrok;

namespace NebulaServer.Services.Core;

public sealed class SystemBootstrapper
{
    private const int MaxApiAttempts = 60;

    private readonly NgrokManager _ngrok;
    private readonly PublicUrlProvider _provider;
    private readonly ILogger<SystemBootstrapper> _logger;

    public SystemBootstrapper(NgrokManager ngrok, PublicUrlProvider provider, ILogger<SystemBootstrapper> logger)
    {
        _ngrok = ngrok;
        _provider = provider;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await WaitForApi();

        await _ngrok.StartAsync();

        if (!_provider.IsReady)
        {
            _logger.LogWarning("Public URL not ready yet. Ngrok tunnel may still be starting or failed to expose a URL. Server will continue running without it.");
        }
        else
        {
            _logger.LogInformation("Public URL is ready: {Url}", _provider.PublicUrl);
        }
    }

    private static async Task WaitForApi()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        for (int i = 0; i < MaxApiAttempts; i++)
        {
            try
            {
                var res = await http.GetAsync("http://localhost:5000/api/system/ready");

                if (res.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // not ready yet
            }

            await Task.Delay(1000);
        }

        // Same idea here — don't throw and take down the server if the API doesn't respond in time.
        // Adjust this to whatever "not ready" behavior you want for the rest of the app.
    }
}