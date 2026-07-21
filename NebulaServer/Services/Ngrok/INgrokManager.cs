using NebulaServer.Models;

namespace NebulaServer.Services.Ngrok;

public interface INgrokManager
{
    Task InitializeAsync();

    Task<bool> ConfigureAsync(string token);

    Task<bool> StartAsync();

    Task StopAsync();

    Task<NgrokStatus> GetStatusAsync();

    Task<string?> GetPublicUrlAsync();
}