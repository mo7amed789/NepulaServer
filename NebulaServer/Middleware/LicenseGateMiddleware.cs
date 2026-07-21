// Middleware/LicenseGateMiddleware.cs
using NebulaServer.Models.Licensing;
using NebulaServer.Services.Licensing;

namespace NebulaServer.Middleware;

public sealed class LicenseGateMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly string[] AllowedPathPrefixes =
    {
        "/setup",
        "/api/pairing",       // بيشمل /api/pairing/license/activate ونظام الـ pairing كله
        "/api/system/ready",
        "/dashboardHub"       // اختياري: سيبه لو الـ hub بيستخدم في شاشة setup نفسها، وإلا امسحه
    };

    public LicenseGateMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ILicenseManager licenseManager)
    {
        if ((licenseManager.CurrentState == LicenseState.Valid ||
            licenseManager.CurrentState == LicenseState.OfflineGrace)
           || IsAllowedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            "{\"success\":false,\"error\":\"license_invalid\",\"message\":\"License Invalid\"}");
    }

    private static bool IsAllowedPath(PathString path) =>
        AllowedPathPrefixes.Any(prefix => path.StartsWithSegments(prefix));
}
