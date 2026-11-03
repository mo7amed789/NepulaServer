using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Media;
using NebulaServer.Providers;

namespace NebulaServer.Services.MediaExtraction.Sources;

public class AuthenticatedSource : IInstagramSource
{
    private readonly HttpClient _httpClient;
    private readonly ICookieProvider _cookieProvider;
    private readonly ILogger<AuthenticatedSource> _logger;

    public AuthenticatedSource(HttpClient httpClient, ICookieProvider cookieProvider, ILogger<AuthenticatedSource> logger)
    {
        _httpClient = httpClient;
        _cookieProvider = cookieProvider;
        _logger = logger;
    }

    public async Task<InstagramMediaData?> ExtractAsync(Uri url, CancellationToken ct = default)
    {
        _logger.LogInformation("Attempting authenticated extraction for Instagram URL: {Url}", url);

        var cookies = await _cookieProvider.GetCookiesAsync("instagram", ct);
        if (string.IsNullOrWhiteSpace(cookies))
        {
            throw new MediaExtractionException(
                ExtractionErrorCode.AuthenticationRequired,
                "Authenticated extraction requires valid cookies, but none were provided by the CookieProvider.");
        }

        // TODO: Implement actual authenticated scraping logic here.
        // e.g. Add cookies to _httpClient default headers and fetch the page/API.
        
        await Task.Delay(100, ct); // Simulate network call
        
        // For now, simulate failure to ensure the pipeline error handling works
        throw new MediaExtractionException(
            ExtractionErrorCode.ExtractionFailed,
            "Authenticated extraction logic is not yet fully implemented.");
    }
}
