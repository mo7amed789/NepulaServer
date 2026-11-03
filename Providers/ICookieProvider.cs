using System.Threading;
using System.Threading.Tasks;

namespace NebulaServer.Providers;

public interface ICookieProvider
{
    Task<string?> GetCookiesAsync(string platform, CancellationToken ct = default);
}
