using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Helpers;
using NebulaServer.Models.ImageEngine;
using NebulaServer.Services.ImageEngine.Extractors;

namespace NebulaServer.Services.ImageEngine
{
    public class ImageExtractionEngine
    {
        private readonly IEnumerable<IImageExtractor> _extractors;

        public ImageExtractionEngine(IEnumerable<IImageExtractor> extractors)
        {
            _extractors = extractors;
        }

        public async Task<ExtractionResult> ExtractAsync(Uri url, CancellationToken cancellationToken = default)
        {
            var candidates = new List<ImageCandidate>();
            
            // 1. Direct Image Check First (to save time if it's already an image)
            var directExtractor = _extractors.OfType<DirectImageExtractor>().FirstOrDefault();
            if (directExtractor != null && directExtractor.CanHandle(url))
            {
                var directCandidates = await directExtractor.ExtractAsync(url, cancellationToken);
                if (directCandidates.Any())
                {
                    // If it's a direct image, we don't need to parse HTML
                    return new ExtractionResult
                    {
                        Title = YouTubeImageUrl.IsSupported(url) ? "YouTube post image" :
                            url.Segments.LastOrDefault()?.TrimEnd('/') ?? "Direct Image",
                        Images = directCandidates
                    };
                }
            }

            // 2. Social Media Extractor Check (Prevent fallback to HTML for Instagram/TikTok etc)
            var socialExtractor = _extractors.OfType<SocialMediaImageExtractor>().FirstOrDefault();
            if (socialExtractor != null && socialExtractor.CanHandle(url))
            {
                var socialCandidates = await socialExtractor.ExtractAsync(url, cancellationToken);
                if (socialCandidates.Any())
                {
                    return new ExtractionResult
                    {
                        Title = "Social Media Image",
                        Images = socialCandidates.ToList()
                    };
                }

                // socialExtractor returned empty — not a hard failure, just no data
                throw new InvalidOperationException(
                    "Unable to extract media from this platform. " +
                    "The post may be private or require authentication (cookies). " +
                    "Place a valid cookies.txt in C:\\ProgramData\\Tanzeel\\ and retry.");
            }

            // 3. Run generic extractors concurrently
            var applicableExtractors = _extractors.Where(e => e.CanHandle(url) && !(e is DirectImageExtractor)).ToList();
            
            var extractionTasks = applicableExtractors.Select(e => e.ExtractAsync(url, cancellationToken));
            var results = await Task.WhenAll(extractionTasks);

            foreach (var resultList in results)
            {
                candidates.AddRange(resultList);
            }

            // 4. Deduplicate, Rank, and Filter
            var rankedCandidates = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                .GroupBy(c => c.Url) // Group by identical URL to deduplicate
                .Select(g => g.OrderByDescending(c => c.Score).First()) // Pick highest score per URL
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Width ?? 0) // Fallback to largest width if scores tie
                .ToList();

            return new ExtractionResult
            {
                Title = "Extracted Images",
                Images = rankedCandidates
            };
        }
    }
}
