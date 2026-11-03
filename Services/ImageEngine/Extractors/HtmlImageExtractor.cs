using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using NebulaServer.Models.ImageEngine;

namespace NebulaServer.Services.ImageEngine.Extractors
{
    public class HtmlImageExtractor : IImageExtractor
    {
        private readonly HttpClient _httpClient;
        
        public HtmlImageExtractor(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("ImageExtractorClient");
        }

        public bool CanHandle(Uri url)
        {
            // HTML Extractor is a fallback, it can theoretically handle any HTTP/S URL
            return url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;
        }

        public async Task<IReadOnlyList<ImageCandidate>> ExtractAsync(Uri url, CancellationToken cancellationToken = default)
        {
            var candidates = new List<ImageCandidate>();
            
            try
            {
                var htmlContent = await _httpClient.GetStringAsync(url, cancellationToken);
                
                var config = Configuration.Default;
                var context = BrowsingContext.New(config);
                var document = await context.OpenAsync(req => req.Content(htmlContent).Address(url.ToString()), cancellationToken);

                // 1. OpenGraph
                ExtractOpenGraph(document, candidates);
                
                // 2. Twitter Card
                ExtractTwitterCard(document, candidates);
                
                // 3. Normal HTML Images (<img src="" srcset="">, <picture>)
                ExtractHtmlImages(document, url, candidates);
            }
            catch (Exception)
            {
                // Ignore exceptions (timeout, parsing errors)
            }

            return candidates;
        }

        private void ExtractOpenGraph(IDocument document, List<ImageCandidate> candidates)
        {
            var ogImage = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
            if (!string.IsNullOrWhiteSpace(ogImage))
            {
                candidates.Add(new ImageCandidate
                {
                    Url = ogImage,
                    Source = "og:image",
                    Score = 100 // High score for explicit metadata
                });
            }
        }

        private void ExtractTwitterCard(IDocument document, List<ImageCandidate> candidates)
        {
            var twitterImage = document.QuerySelector("meta[name='twitter:image']")?.GetAttribute("content");
            if (!string.IsNullOrWhiteSpace(twitterImage))
            {
                candidates.Add(new ImageCandidate
                {
                    Url = twitterImage,
                    Source = "twitter:image",
                    Score = 90
                });
            }
        }

        private void ExtractHtmlImages(IDocument document, Uri baseUrl, List<ImageCandidate> candidates)
        {
            var imgElements = document.QuerySelectorAll<IHtmlImageElement>("img");
            
            foreach (var img in imgElements)
            {
                var src = img.Source;
                var srcset = img.GetAttribute("srcset");
                
                if (!string.IsNullOrWhiteSpace(srcset))
                {
                    // Basic parsing of srcset to get the highest resolution (simplified)
                    // Format: "url 1x, url 2x" or "url 480w, url 1024w"
                    var bestUrl = ParseBestSrcSet(srcset, baseUrl, out int? bestWidth);
                    if (bestUrl != null)
                    {
                        candidates.Add(new ImageCandidate
                        {
                            Url = bestUrl,
                            Source = "img srcset",
                            Width = bestWidth,
                            Score = 70 + (bestWidth.HasValue ? Math.Min(30, bestWidth.Value / 100) : 0), // Base 70 + bonus for size
                            RawSourceSnippet = srcset
                        });
                        continue; // If we got a srcset, prefer it over src
                    }
                }
                
                if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                {
                    // Handle relative URLs
                    if (Uri.TryCreate(baseUrl, src, out var absoluteUri))
                    {
                        candidates.Add(new ImageCandidate
                        {
                            Url = absoluteUri.ToString(),
                            Source = "img src",
                            Score = 50 // Standard score for plain img
                        });
                    }
                }
            }
        }

        private string? ParseBestSrcSet(string srcset, Uri baseUrl, out int? width)
        {
            width = null;
            string? bestUrl = null;
            int maxResolution = 0;

            var entries = srcset.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var parts = entry.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var url = parts[0];
                    int resolution = 1; // Default
                    
                    if (parts.Length > 1)
                    {
                        var descriptor = parts[1].ToLowerInvariant();
                        if (descriptor.EndsWith("w") && int.TryParse(descriptor.TrimEnd('w'), out int w))
                        {
                            resolution = w;
                        }
                        else if (descriptor.EndsWith("x") && int.TryParse(descriptor.TrimEnd('x'), out int x))
                        {
                            resolution = x * 1000; // Arbitrary scale to prefer x over no descriptor
                        }
                    }

                    if (resolution >= maxResolution)
                    {
                        maxResolution = resolution;
                        
                        if (Uri.TryCreate(baseUrl, url, out var absoluteUri))
                        {
                            bestUrl = absoluteUri.ToString();
                            width = descriptorEndsWithW(parts) ? resolution : (int?)null;
                        }
                    }
                }
            }

            return bestUrl;

            bool descriptorEndsWithW(string[] parts)
            {
                return parts.Length > 1 && parts[1].ToLowerInvariant().EndsWith("w");
            }
        }
    }
}
