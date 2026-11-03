using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace NebulaServer.Middleware;

public sealed class PairingAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PairingAuthenticationMiddleware> _logger;

    public PairingAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<PairingAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Allow loopback requests (Photino UI)
        if (context.Connection.RemoteIpAddress != null && System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
        {
            await _next(context);
            return;
        }

        // Allow legacy X-Nebula-Key for SignalR or frontend
        if (context.Request.Headers.TryGetValue("X-Nebula-Key", out var headerKey) && !string.IsNullOrEmpty(headerKey))
        {
            var pairingService = context.RequestServices.GetRequiredService<NebulaServer.Services.Pairing.IPairingService>();
            if (await pairingService.ValidateKeyAsync(headerKey.ToString()))
            {
                await _next(context);
                return;
            }
        }

        _logger.LogWarning(
            "Authentication rejected for {Method} {Path}. Valid JWT Bearer token required.",
            context.Request.Method,
            context.Request.Path.Value);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            error = "Missing or invalid bearer token."
        });
    }
}
