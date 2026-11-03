using NebulaServer.Helpers;
using NebulaServer.Models.ImageEngine;

namespace NebulaServer.Services.ImageEngine.Extractors;

public sealed class DirectImageExtractor : IImageExtractor
{
    private readonly HttpClient _httpClient;
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".svg", ".avif" };

    public DirectImageExtractor(IHttpClientFactory factory)
    {
        // Retains connection-time address validation, including redirects.
        _httpClient = factory.CreateClient("ImageExtractorClient");
    }

    public bool CanHandle(Uri url) => YouTubeImageUrl.IsSupported(url) ||
        ImageExtensions.Any(ext => url.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<ImageCandidate>> ExtractAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var original = YouTubeImageUrl.OriginalSize(url);
        var candidate = await ProbeAsync(original, cancellationToken);
        if (candidate is null && original != url)
            candidate = await ProbeAsync(url, cancellationToken);
        return candidate is null ? Array.Empty<ImageCandidate>() : new[] { candidate };
    }

    private async Task<ImageCandidate?> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        // Some CDNs reject HEAD. GET reads headers only; the downloader later
        // validates file signatures and enforces its existing size limit.
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                using var response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (response.IsSuccessStatusCode &&
                    contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new ImageCandidate
                    {
                        Url = url.AbsoluteUri,
                        MimeType = contentType,
                        FileSize = response.Content.Headers.ContentLength,
                        Source = YouTubeImageUrl.IsSupported(url) ? "YouTube image" : "Direct URL",
                        Score = 1000
                    };
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (HttpRequestException) { }
        }
        return null;
    }
}
