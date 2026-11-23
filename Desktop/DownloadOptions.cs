using System.Globalization;
using NebulaServer.Helpers;
using NebulaServer.Models;
using NebulaServer.Services;

namespace NebulaServer.Desktop;

internal static class DownloadOptions
{
    public static string ValidateUrl(string input)
    {
        var url = StorageService.NormalizeInputUrl(input.Trim());
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid HTTP or HTTPS media link.");
        return url;
    }

    public static void Validate(JobRequest request)
    {
        request.Url = ValidateUrl(request.Url);
        if (request.Format is not ("mp4" or "mp3"))
            throw new ArgumentException("Choose MP4 video or MP3 audio.");
        if (!ProxyAddress.TryNormalize(request.Proxy, out var normalizedProxy))
            throw new ArgumentException("Proxy must be a valid HTTP or HTTPS URL.");
        request.Proxy = normalizedProxy;
        if (request.IsPlaylist && request.PlaylistEnd is > 0 && request.PlaylistEnd < request.PlaylistStart)
            throw new ArgumentException("The last playlist item must be at least the first item.");
        var start = ParseTime(request.SegmentStart);
        var end = ParseTime(request.SegmentEnd);
        if (end.HasValue && end <= (start ?? TimeSpan.Zero))
            throw new ArgumentException("The clip end must be later than its start.");
    }

    public static bool TryValidateClipboardUrl(string? input, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            url = ValidateUrl(input);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // yt-dlp accepts seconds or a colon-separated timestamp.
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        var parts = value.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, out var n) && n >= 0))
        {
            var numbers = parts.Select(int.Parse).ToArray();
            if (numbers[^1] < 60 && (parts.Length == 2 || numbers[^2] < 60))
                return TimeSpan.FromSeconds(numbers[^1] + numbers[^2] * 60d + (parts.Length == 3 ? numbers[0] * 3600d : 0));
        }
        throw new ArgumentException("Clip times must be seconds, MM:SS, or HH:MM:SS.");
    }
}
