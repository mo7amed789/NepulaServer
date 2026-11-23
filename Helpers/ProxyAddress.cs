using System;
using System.Net;

namespace NebulaServer.Helpers;

public static class ProxyAddress
{
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var text = value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return false;

        normalized = uri.AbsoluteUri;
        return true;
    }

    public static IWebProxy CreateWebProxy(string value)
    {
        if (!TryNormalize(value, out var normalized) || normalized is null)
            throw new ArgumentException("Proxy must be a valid HTTP or HTTPS URL.", nameof(value));

        var uri = new Uri(normalized, UriKind.Absolute);
        var proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':');
            var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            var password = separator < 0 ? string.Empty : uri.UserInfo[(separator + 1)..];
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(user),
                Uri.UnescapeDataString(password));
        }

        return proxy;
    }
}
