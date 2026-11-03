using System.Text.RegularExpressions;

namespace NebulaServer.Helpers;

public static partial class YouTubeImageUrl
{
    public static bool IsSupported(Uri url) =>
        (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) &&
        string.IsNullOrEmpty(url.UserInfo) && HostPattern().IsMatch(url.IdnHost);

    public static Uri OriginalSize(Uri url)
    {
        if (!IsSupported(url)) return url;
        var path = url.AbsolutePath;
        var match = SizeSuffix().Match(path);
        if (!match.Success) return url;
        return new UriBuilder(url) { Path = path[..match.Index] + "=s0" }.Uri;
    }

    [GeneratedRegex(@"^yt[0-9]+\.(?:ggpht|googleusercontent)\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"=(?:s|w|h)[0-9]+(?:-.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeSuffix();
}
