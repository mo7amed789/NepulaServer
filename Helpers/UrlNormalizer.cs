using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace NebulaServer.Helpers;

/// <summary>
/// Normalizes and validates user-supplied media URLs.
/// </summary>
public static class UrlNormalizer
{
    private static readonly string[] SocialMediaDomains =
    {
        "instagram.com", "instagr.am",
        "facebook.com", "fb.com", "fb.watch",
        "twitter.com", "x.com",
        "tiktok.com", "vm.tiktok.com",
        "snapchat.com", "snap.com",
        "reddit.com", "pinterest.com",
    };

    public static string NormalizeInputUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        input = input.Trim();

        // Markdown link: [https://example.com](https://example.com)
        var markdownMatch = Regex.Match(
            input,
            @"\]\((https?://[^)]+)\)",
            RegexOptions.IgnoreCase);

        if (markdownMatch.Success)
            return markdownMatch.Groups[1].Value;

        // [https://example.com]
        var bracketMatch = Regex.Match(
            input,
            @"\[(https?://[^\]]+)\]",
            RegexOptions.IgnoreCase);

        if (bracketMatch.Success)
            return bracketMatch.Groups[1].Value;

        // Instagram URLs normalization:
        // 1. instagr.am -> www.instagram.com
        input = Regex.Replace(input, @"^https?://(?:www\.)?instagr\.am/", "https://www.instagram.com/", RegexOptions.IgnoreCase);

        // 2. /share/reel/ or /share/p/ or /share/tv/ -> /reel/ or /p/ or /tv/
        input = Regex.Replace(input, @"/share/(reels?|p|tv)/([^/?#&]+)", "/$1/$2", RegexOptions.IgnoreCase);

        // 3. /reels/ -> /reel/
        input = Regex.Replace(input, @"/reels/([^/?#&]+)", "/reel/$1", RegexOptions.IgnoreCase);

        // 4. Strip tracking parameters (?igsh=..., utm_...) from Instagram URLs
        if (input.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            var qIdx = input.IndexOf('?');
            if (qIdx > 0)
            {
                var query = input[(qIdx + 1)..];
                if (query.Contains("igsh=", StringComparison.OrdinalIgnoreCase) ||
                    query.Contains("utm_", StringComparison.OrdinalIgnoreCase))
                {
                    input = input[..qIdx];
                }
            }

            // Ensure trailing slash for post/reel IDs if missing
            if (Regex.IsMatch(input, @"/(?:p|tv|reel)/[^/?#]+$"))
            {
                input += "/";
            }
        }

        return input;
    }

    public static bool IsValidHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        url = url.Trim();

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    public static bool IsSocialMediaUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return SocialMediaDomains.Any(d => host == d || host.EndsWith("." + d));
        }
        catch
        {
            return false;
        }
    }
}
