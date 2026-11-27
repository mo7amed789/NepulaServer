using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;

namespace NebulaServer.Services.Preview;

/// <summary>
/// Extracts preview image URLs from social media HTML pages via OpenGraph/Twitter meta tags.
/// </summary>
public sealed class SocialMediaMetadataExtractor
{
    private readonly ILogger _logger;

    public SocialMediaMetadataExtractor(ILogger<SocialMediaMetadataExtractor> logger)
        : this((ILogger)logger)
    {
    }

    public SocialMediaMetadataExtractor(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<string?> ExtractImageUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        url = UrlNormalizer.NormalizeInputUrl(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language",
            "en-US,en;q=0.9");

        var cookiesPath = Path.Combine(RuntimePaths.Data, "cookies.txt");
        if (File.Exists(cookiesPath))
        {
            var cookieHeader = BuildCookieHeaderFromNetscapeFile(cookiesPath, uri);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Social media metadata request returned {StatusCode} for {Url}", (int)response.StatusCode, url);
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var imageUrl = ExtractMetaImage(html, "property", "og:image");
        if (UrlNormalizer.IsValidHttpUrl(imageUrl))
        {
            _logger.LogInformation("Found social media image through og:image: {ImageUrl}", imageUrl);
            return imageUrl;
        }

        imageUrl = ExtractMetaImage(html, "name", "twitter:image");
        if (UrlNormalizer.IsValidHttpUrl(imageUrl))
        {
            _logger.LogInformation("Found social media image through twitter:image: {ImageUrl}", imageUrl);
            return imageUrl;
        }

        imageUrl = ExtractDisplayUrl(html);
        if (UrlNormalizer.IsValidHttpUrl(imageUrl))
        {
            _logger.LogInformation("Found social media image through display_url: {ImageUrl}", imageUrl);
            return imageUrl;
        }

        _logger.LogWarning("Could not find social media image metadata for {Url}", url);
        return null;
    }

    private static string? ExtractMetaImage(string html, string attribute, string value)
    {
        var escapedValue = Regex.Escape(value);
        var patterns = new[]
        {
            $@"<meta\s+[^>]*{attribute}\s*=\s*[""']{escapedValue}[""'][^>]*content\s*=\s*[""']([^""']+)[""']",
            $@"<meta\s+[^>]*content\s*=\s*[""']([^""']+)[""'][^>]*{attribute}\s*=\s*[""']{escapedValue}[""']"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) continue;

            var result = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (UrlNormalizer.IsValidHttpUrl(result)) return result;
        }
        return null;
    }

    private static string? ExtractDisplayUrl(string html)
    {
        var patterns = new[]
        {
            @"""display_url""\s*:\s*""([^""]+)""",
            @"""displayUrl""\s*:\s*""([^""]+)"""
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var imageUrl = match.Groups[1].Value
                .Replace("\\/", "/")
                .Replace("\\u0026", "&");

            imageUrl = Regex.Unescape(imageUrl);
            if (UrlNormalizer.IsValidHttpUrl(imageUrl)) return imageUrl;
        }
        return null;
    }

    private static string? BuildCookieHeaderFromNetscapeFile(string path, Uri targetUri)
    {
        var host = targetUri.Host.TrimStart('.').ToLowerInvariant();
        var cookies = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;

            var domain = parts[0].Trim().TrimStart('.').ToLowerInvariant();
            var name = parts[5].Trim();
            var value = parts[6].Trim();

            if (string.IsNullOrWhiteSpace(name)) continue;

            if (host == domain || host.EndsWith("." + domain))
                cookies.Add($"{name}={value}");
        }

        return cookies.Count == 0 ? null : string.Join("; ", cookies);
    }
}
