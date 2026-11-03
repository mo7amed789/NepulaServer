using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Media;
using NebulaServer.Services.MediaExtraction.Sources;

namespace NebulaServer.Services.MediaExtraction.Extractors;

public class InstagramExtractor : IMediaExtractor
{
    private readonly IEnumerable<IInstagramSource> _sources;
    private readonly ILogger<InstagramExtractor> _logger;

    // The order of sources in DI will dictate priority (e.g. PublicWebSource first, then AuthenticatedSource).
    public InstagramExtractor(IEnumerable<IInstagramSource> sources, ILogger<InstagramExtractor> logger)
    {
        _sources = sources;
        _logger = logger;
    }

    public bool CanHandle(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return host == "instagram.com" || host.EndsWith(".instagram.com") || host == "instagr.am";
    }

    public async Task<MediaResult> ExtractAsync(Uri url, CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            try
            {
                var data = await source.ExtractAsync(url, ct);
                if (data != null)
                {
                    _logger.LogInformation("Successfully extracted Instagram media using source: {SourceName}", source.GetType().Name);
                    
                    return new MediaResult
                    {
                        Id = data.Id,
                        Title = data.Title,
                        Thumbnail = data.Thumbnail,
                        Duration = data.Duration,
                        Platform = "Instagram",
                        Formats = data.Formats
                    };
                }
            }
            catch (MediaExtractionException ex) when (ex.Code == ExtractionErrorCode.AuthenticationRequired)
            {
                _logger.LogWarning("Source {SourceName} requires authentication. Trying next source...", source.GetType().Name);
                // Continue to next source (e.g. fallback to AuthenticatedSource)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source {SourceName} failed unexpectedly.", source.GetType().Name);
                // Continue to next source
            }
        }

        // If we exhausted all sources
        throw new MediaExtractionException(
            ExtractionErrorCode.ExtractionFailed,
            "Failed to extract media from Instagram. The post might be private, deleted, or require a valid session cookie.");
    }
}
