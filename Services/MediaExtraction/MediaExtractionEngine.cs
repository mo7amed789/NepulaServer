using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Helpers;
using NebulaServer.Models.Media;

namespace NebulaServer.Services.MediaExtraction;

public sealed class MediaExtractionEngine
{
    private readonly IEnumerable<IMediaExtractor> _extractors;

    public MediaExtractionEngine(IEnumerable<IMediaExtractor> extractors)
    {
        _extractors = extractors;
    }

    public async Task<MediaResult> ExtractAsync(Uri url, CancellationToken ct = default)
    {
        var extractor = _extractors.FirstOrDefault(x => x.CanHandle(url));

        if (extractor is null)
        {
            throw new MediaExtractionException(
                ExtractionErrorCode.UnsupportedUrl,
                "Unsupported media platform or URL format.");
        }

        return await extractor.ExtractAsync(url, ct);
    }
}
