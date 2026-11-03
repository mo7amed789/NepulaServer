using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;
using NebulaServer.Models.ImageEngine;

namespace NebulaServer.Services.ImageEngine.Extractors;

/// <summary>
/// Extracts the actual photos from social posts. yt-dlp represents Instagram
/// carousels and TikTok photo posts as entries, so parsing the complete JSON is
/// required; a preview-only request returns just one thumbnail.
/// </summary>
public sealed class SocialMediaImageExtractor : IImageExtractor
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "gif", "bmp", "tif", "tiff", "avif"
    };

    private static readonly string[] SupportedDomains =
    {
        "instagram.com", "instagr.am",
        "tiktok.com", "vm.tiktok.com", "vt.tiktok.com",
        "facebook.com", "fb.com", "fb.watch",
        "twitter.com", "x.com",
        "reddit.com", "redd.it",
        "pinterest.com", "pin.it",
        "snapchat.com", "snap.com",
        "youtube.com", "youtu.be",
        "threads.net", "tumblr.com"
    };

    private readonly ILogger<SocialMediaImageExtractor> _logger;
    private readonly HttpClient _httpClient;

    public SocialMediaImageExtractor(
        IHttpClientFactory httpClientFactory,
        ILogger<SocialMediaImageExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("ImageExtractorClient");
        _logger = logger;
    }

    public bool CanHandle(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return SupportedDomains.Any(domain => host == domain || host.EndsWith('.' + domain));
    }

    public async Task<IReadOnlyList<ImageCandidate>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        // Instagram's regular post page increasingly returns a login shell for
        // public image posts. The lightweight embed endpoint still exposes the
        // signed CDN image URL, so try it before yt-dlp for image posts.
        if (url.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) ||
            url.Host.Contains("instagr.am", StringComparison.OrdinalIgnoreCase))
        {
            var embeddedImages = await ExtractInstagramEmbedImagesAsync(url, cancellationToken);
            if (embeddedImages.Count > 0) return embeddedImages;
        }

        if (url.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            var photoPostImages = await ExtractTikTokPhotoPostAsync(url, cancellationToken);
            if (photoPostImages.Count > 0) return photoPostImages;
        }

        var cookiesPath = FindCookiesPath();
        var attempts = cookiesPath is null ? new string?[] { null } : new string?[] { null, cookiesPath };
        string lastError = string.Empty;

        foreach (var attemptCookies in attempts)
        {
            var extraction = await RunExtractorAsync(url, attemptCookies, cancellationToken);
            if (extraction.ExitCode != 0)
            {
                lastError = extraction.Error;
                _logger.LogWarning(
                    "Social image extraction failed for {Url} using {Mode}: {Error}",
                    url, attemptCookies is null ? "public access" : "cookies", lastError.Trim());
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(extraction.Output);
                var candidates = ParseCandidates(document.RootElement);
                if (candidates.Count > 0) return candidates;

                lastError = "The post did not expose any downloadable images. It may be private or video-only.";
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "yt-dlp returned invalid JSON for {Url}", url);
                lastError = "The platform returned media data in an unexpected format.";
            }
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ?
            "The platform could not provide this post." : ToFriendlyError(lastError));
    }

    private async Task<List<ImageCandidate>> ExtractInstagramEmbedImagesAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = new UriBuilder(url)
            {
                Query = string.Empty,
                Fragment = string.Empty,
                Path = url.AbsolutePath.TrimEnd('/') + "/embed/"
            };
            // Instagram serves a login shell to Chromium-style user agents on
            // this endpoint. Use a neutral browser identity for the embed,
            // which is the same public response used by normal embeds.
            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = new List<ImageCandidate>();
            var matches = Regex.Matches(
                html,
                @"<img[^>]+class=[""'][^""']*EmbeddedMediaImage[^""']*[""'][^>]+src=[""'](?<url>[^""']+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            foreach (Match match in matches)
            {
                var imageUrl = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) ||
                    (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
                    continue;

                result.Add(new ImageCandidate
                {
                    Url = imageUrl,
                    Source = "Instagram embed image",
                    Score = 700 - Math.Min(result.Count, 100)
                });
            }

            return result
                .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _logger.LogDebug(ex, "Instagram embed extraction did not yield images for {Url}; using yt-dlp fallback", url);
            return new List<ImageCandidate>();
        }
    }

    private async Task<List<ImageCandidate>> ExtractTikTokPhotoPostAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url, cancellationToken);
            var scriptMatch = Regex.Match(
                html,
                "<script[^>]+id=[\\\"'](?:__UNIVERSAL_DATA_FOR_REHYDRATION__|SIGI_STATE)[\\\"'][^>]*>(?<json>[\\s\\S]*?)</script>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!scriptMatch.Success) return new List<ImageCandidate>();

            var json = WebUtility.HtmlDecode(scriptMatch.Groups["json"].Value);
            using var document = JsonDocument.Parse(json);
            var imagePosts = new List<JsonElement>();
            FindProperties(document.RootElement, "imagePost", imagePosts);

            var result = new List<ImageCandidate>();
            var position = 0;
            foreach (var imagePost in imagePosts)
            {
                if (!imagePost.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var image in images.EnumerateArray())
                {
                    var imageUrl = FindFirstUrlListValue(image);
                    if (string.IsNullOrWhiteSpace(imageUrl)) continue;
                    result.Add(new ImageCandidate
                    {
                        Url = imageUrl,
                        Width = FindFirstNumber(image, "width"),
                        Height = FindFirstNumber(image, "height"),
                        Source = "TikTok photo post",
                        Score = 600 - Math.Min(position++, 100)
                    });
                }
            }

            return result
                .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "TikTok photo-page extraction did not yield images for {Url}; using yt-dlp fallback", url);
            return new List<ImageCandidate>();
        }
    }

    private static void FindProperties(JsonElement node, string propertyName, List<JsonElement> matches)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals(propertyName)) matches.Add(property.Value);
                FindProperties(property.Value, propertyName, matches);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) FindProperties(item, propertyName, matches);
        }
    }

    private static string? FindFirstUrlListValue(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if ((property.NameEquals("urlList") || property.NameEquals("url_list")) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String &&
                            Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                            return item.GetString();
                    }
                }

                var nested = FindFirstUrlListValue(property.Value);
                if (nested is not null) return nested;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var nested = FindFirstUrlListValue(item);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static int? FindFirstNumber(JsonElement node, string propertyName)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in node.EnumerateObject())
        {
            if (property.NameEquals(propertyName) && property.Value.TryGetInt32(out var value)) return value;
            var nested = FindFirstNumber(property.Value, propertyName);
            if (nested.HasValue) return nested;
        }
        return null;
    }

    private static async Task<(int ExitCode, byte[] Output, string Error)> RunExtractorAsync(
        Uri url,
        string? cookiesPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = BuildStartInfo(url, cookiesPath) };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to start the social-media extractor: {ex.Message}", ex);
        }

        using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cancellationToken));
        return (process.ExitCode, output.ToArray(), await errorTask);
    }

    internal static List<ImageCandidate> ParseCandidates(JsonElement root)
    {
        var candidates = new List<ImageCandidate>();
        CollectLeafCandidates(root, candidates, 0);

        return candidates
            .Where(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri)
                                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
    }

    private static void CollectLeafCandidates(JsonElement item, List<ImageCandidate> candidates, int position)
    {
        if (item.ValueKind != JsonValueKind.Object) return;

        if (item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                CollectLeafCandidates(entry, candidates, position + index++);
            }
            return;
        }

        var before = candidates.Count;
        AddImageUrl(item, "url", candidates, position, 500);

        if (item.TryGetProperty("requested_downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var download in downloads.EnumerateArray())
            {
                AddImageUrl(download, "url", candidates, position, 480);
            }
        }

        if (item.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                AddImageUrl(format, "url", candidates, position, 450);
            }
        }

        // For video posts, keep the largest thumbnail. Photo entries normally
        // take the higher-scored direct-media path above.
        if (candidates.Count == before)
        {
            var thumbnail = FindBestThumbnail(item);
            if (thumbnail is not null) candidates.Add(thumbnail);
        }
    }

    private static void AddImageUrl(
        JsonElement item,
        string propertyName,
        List<ImageCandidate> candidates,
        int position,
        int baseScore)
    {
        if (!item.TryGetProperty(propertyName, out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
            return;

        var url = urlElement.GetString();
        var extension = GetString(item, "ext") ?? GetUrlExtension(url);
        if (string.IsNullOrWhiteSpace(url) || !ImageExtensions.Contains(extension ?? string.Empty)) return;

        candidates.Add(new ImageCandidate
        {
            Url = url,
            Width = GetInt32(item, "width"),
            Height = GetInt32(item, "height"),
            FileSize = GetInt64(item, "filesize") ?? GetInt64(item, "filesize_approx"),
            MimeType = extension is null ? null : $"image/{(extension == "jpg" ? "jpeg" : extension)}",
            Source = "Social post media (yt-dlp)",
            Score = baseScore - Math.Min(position, 100)
        });
    }

    private static ImageCandidate? FindBestThumbnail(JsonElement item)
    {
        if (item.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
        {
            return thumbnails.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new ImageCandidate
                {
                    Url = GetString(value, "url") ?? string.Empty,
                    Width = GetInt32(value, "width"),
                    Height = GetInt32(value, "height"),
                    Source = "Social post thumbnail (yt-dlp)",
                    Score = 200 + (GetInt32(value, "width") ?? 0) / 100
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Url))
                .OrderByDescending(candidate => (long)(candidate.Width ?? 0) * (candidate.Height ?? 0))
                .FirstOrDefault();
        }

        var thumbnailUrl = GetString(item, "thumbnail");
        return string.IsNullOrWhiteSpace(thumbnailUrl)
            ? null
            : new ImageCandidate { Url = thumbnailUrl, Source = "Social post thumbnail (yt-dlp)", Score = 200 };
    }

    private static ProcessStartInfo BuildStartInfo(Uri url, string? cookiesPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RuntimePaths.ResolvePythonExecutable(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RuntimePaths.Base
        };

        foreach (var argument in new[]
                 {
                     "-m", "yt_dlp", "--no-config", "--no-check-certificates", "--no-warnings",
                     "--skip-download", "--dump-single-json", "--js-runtimes", "node",
                     "--remote-components", "ejs:github"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (cookiesPath is not null)
        {
            startInfo.ArgumentList.Add("--cookies");
            startInfo.ArgumentList.Add(cookiesPath);
        }

        startInfo.ArgumentList.Add(url.ToString());
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        if (RuntimePaths.PythonSitePackages is { } packages && Directory.Exists(packages))
        {
            startInfo.Environment["PYTHONPATH"] = packages;
        }

        return startInfo;
    }

    private static string? FindCookiesPath() => new[]
    {
        Path.Combine(RuntimePaths.Data, "cookies.txt"),
        Path.Combine(RuntimePaths.Base, "cookies.txt")
    }.FirstOrDefault(File.Exists);

    private static string ToFriendlyError(string error)
    {
        var lower = error.ToLowerInvariant();
        if (lower.Contains("login") || lower.Contains("sign in") || lower.Contains("cookie"))
            return "This post requires login. Export cookies.txt to C:\\ProgramData\\Tanzeel and try again.";
        if (lower.Contains("private") || lower.Contains("not available") || lower.Contains("does not exist"))
            return "This post is private, unavailable, or has been deleted.";
        return "The platform could not provide this post. Try again or add valid cookies.txt for private/age-restricted media.";
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt32(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? GetInt64(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? GetUrlExtension(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
    }
}
