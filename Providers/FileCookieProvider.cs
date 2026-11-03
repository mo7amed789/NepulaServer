using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Services;
using NebulaServer.Helpers;

namespace NebulaServer.Providers;

public class FileCookieProvider : ICookieProvider
{
    public async Task<string?> GetCookiesAsync(string platform, CancellationToken ct = default)
    {
        // Try ProgramData cookies first
        var programDataCookies = Path.Combine(RuntimePaths.Data, "cookies.txt");
        if (File.Exists(programDataCookies))
        {
            return await File.ReadAllTextAsync(programDataCookies, ct);
        }

        // Try AppBase cookies second
        var appBaseCookies = Path.Combine(RuntimePaths.Base, "cookies.txt");
        if (File.Exists(appBaseCookies))
        {
            return await File.ReadAllTextAsync(appBaseCookies, ct);
        }

        return null;
    }
}
