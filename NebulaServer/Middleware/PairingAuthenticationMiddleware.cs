using Microsoft.Extensions.Logging;
using NebulaServer.Models.Pairing;
using NebulaServer.Services.Pairing;

namespace NebulaServer.Middleware;

public sealed class PairingAuthenticationMiddleware
{
    private const string HeaderName = "X-Nebula-Key";

    private readonly RequestDelegate _next;
    private readonly ILogger<PairingAuthenticationMiddleware> _logger;

    public PairingAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<PairingAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IPairingService pairingService)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Public endpoints
        if (IsPublicEndpoint(path))
        {
            await _next(context);
            return;
        }

        var headerPresent = context.Request.Headers.TryGetValue(HeaderName, out var headerValues);
        var receivedKey = headerPresent ? headerValues.ToString() : null;

        PairingInfo pairing;

        try
        {
            pairing = await pairingService.GetPairingInfoAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load pairing information.");

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                error = "Unable to load pairing state."
            });

            return;
        }

        bool comparisonResult =
            headerPresent &&
            !string.IsNullOrWhiteSpace(receivedKey) &&
            PairingService.ConstantTimeEquals(pairing.PairingKey, receivedKey);

        if (!comparisonResult)
        {
            _logger.LogWarning(
                "Pairing authentication rejected for {Method} {Path}. HeaderPresent={HeaderPresent}",
                context.Request.Method,
                path,
                headerPresent);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;

            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                error = headerPresent
                    ? "Invalid pairing key."
                    : "Missing pairing key."
            });

            return;
        }

        await _next(context);
    }

    private static bool IsPublicEndpoint(string path)
    {
        return
            IsExactOrSubpath(path, "/") ||
            IsExactOrSubpath(path, "/swagger") ||
            IsExactOrSubpath(path, "/setup") ||
            IsExactOrSubpath(path, "/api/system/ready") ||
            IsExactOrSubpath(path, "/api/system/recovery") ||

            // Pairing
            IsExactOrSubpath(path, "/api/pairing") ||

            // Dashboard
            IsExactOrSubpath(path, "/api/dashboard") ||
            IsExactOrSubpath(path, "/api/dashboardapp") ||

            // QR
            IsExactOrSubpath(path, "/api/dashboard/qr") ||

            // Server
            IsExactOrSubpath(path, "/api/server") ||

            // Favicon
            IsExactOrSubpath(path, "/favicon.ico");
    }

    private static bool IsExactOrSubpath(string path, string prefix)
    {
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
