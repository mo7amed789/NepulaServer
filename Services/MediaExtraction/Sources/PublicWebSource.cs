using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Media;

namespace NebulaServer.Services.MediaExtraction.Sources;

public class PublicWebSource : IInstagramSource
{
    private readonly StorageService _storageService;
    private readonly ILogger<PublicWebSource> _logger;

    public PublicWebSource(StorageService storageService, ILogger<PublicWebSource> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    public async Task<InstagramMediaData?> ExtractAsync(Uri url, CancellationToken ct = default)
    {
        _logger.LogInformation("Attempting public web extraction for Instagram URL: {Url}", url);
        try
        {
            var preview = await _storageService.ExtractPreviewDataAsync(url.ToString());
            if (preview != null && (!string.IsNullOrWhiteSpace(preview.Title) || !string.IsNullOrWhiteSpace(preview.ThumbnailUrl)))
            {
                var formats = new List<MediaFormat>();
                foreach (var q in preview.AvailableQualities)
                {
                    formats.Add(new MediaFormat
                    {
                        Id = q.Label,
                        Url = url.ToString(),
                        Quality = q.Label + "p",
                        Extension = q.Label == "audio" ? "mp3" : "mp4",
                        Size = q.FilesizeBytes,
                        HasAudio = true,
                        HasVideo = q.Label != "audio"
                    });
                }

                return new InstagramMediaData
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Title = preview.Title ?? "Instagram Video",
                    Thumbnail = preview.ThumbnailUrl,
                    Formats = formats
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PublicWebSource yt-dlp preview failed for {Url}", url);
        }

        throw new MediaExtractionException(
            ExtractionErrorCode.AuthenticationRequired,
            "Public extraction failed. Instagram is blocking unauthenticated requests.");
    }
}
