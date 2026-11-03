using System.Net;
using System.Net.Sockets;

namespace NebulaServer.Helpers;

/// <summary>
/// Creates HTTP handlers that resolve and validate the exact address used for
/// each connection. This prevents redirects and DNS rebinding from reaching
/// loopback, private, link-local, multicast, or reserved networks.
/// </summary>
public static class SafeOutboundHttp
{
    public static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,

            // A system proxy would hide the destination address from
            // ConnectCallback, preventing connection-time SSRF enforcement.
            UseProxy = false,
            ConnectCallback = ConnectToValidatedAddressAsync
        };
    }

    public static async Task EnsureSafeUriAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Only HTTP and HTTPS URLs are allowed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("URLs containing credentials are not allowed.");
        }

        _ = await ResolvePublicAddressesAsync(uri.DnsSafeHost, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectToValidatedAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken);

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException(
            $"Unable to connect to the validated destination '{context.DnsEndPoint.Host}'.",
            lastError);
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            addresses = [literalAddress];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch (SocketException ex)
            {
                throw new HttpRequestException(
                    $"The destination '{host}' could not be resolved safely.",
                    ex);
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"The destination '{host}' did not resolve to an address.");
        }

        // Reject the whole hostname if any answer is unsafe. Selecting only a
        // public answer would leave mixed-answer DNS rebinding opportunities.
        if (addresses.Any(IsBlockedAddress))
        {
            throw new InvalidOperationException(
                $"The destination '{host}' resolves to a non-public network address.");
        }

        return addresses;
    }

    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];

            return first == 0 ||
                   first == 10 ||
                   first == 127 ||
                   (first == 100 && second is >= 64 and <= 127) ||
                   (first == 169 && second == 254) ||
                   (first == 172 && second is >= 16 and <= 31) ||
                   (first == 192 && second == 0 && bytes[2] == 0) ||
                   (first == 192 && second == 0 && bytes[2] == 2) ||
                   (first == 192 && second == 168) ||
                   (first == 198 && second is 18 or 19) ||
                   (first == 198 && second == 51 && bytes[2] == 100) ||
                   (first == 203 && second == 0 && bytes[2] == 113) ||
                   first >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   address.IsIPv6Multicast ||
                   (bytes[0] & 0xfe) == 0xfc ||
                   (bytes[0] == 0x20 && bytes[1] == 0x01 &&
                    bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        return true;
    }
}
