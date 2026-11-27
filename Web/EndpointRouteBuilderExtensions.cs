using System.Text.Encodings.Web;
using System.Text.Json;
using NebulaServer.Helpers;
using NebulaServer.Hubs;
using NebulaServer.Models.Pairing;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using NebulaServer.Services.Pairing;
using NebulaServer.Services.Server;
using QRCoder;
namespace NebulaServer.Web;

public static class EndpointRouteBuilderExtensions
{
    public static void MapNebulaEndpoints(this WebApplication app)
    {
        var embedJsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default };
        var appPagePath = Path.Combine(AppContext.BaseDirectory, "Html", "app.min.html");
        var imagesPagePath = Path.Combine(AppContext.BaseDirectory, "Html", "images.min.html");
        app.MapHub<DashboardHub>("/dashboardHub");
        app.MapControllers();

        // Open the main application directly.  Keep the legacy setup URL as a
        // redirect so old shortcuts do not send users back to the license page.
        app.MapGet("/", () => Results.Redirect("/app"));
        app.MapGet("/setup", () => Results.Redirect("/app"));

        app.MapGet("/app", () =>
        {
            return PageFile(appPagePath);
        });

        app.MapGet("/images", () =>
        {
            return PageFile(imagesPagePath);
        });

        app.MapGet("/pairing", async (ServerStateService serverState, IPairingService pairingService) =>
        {
            var state = serverState.Get();
            var pairing = await pairingService.GetPairingInfoAsync();
            var payload = new PairingPayload
            {
                ServerId = pairing.ServerId,
                PairingKey = pairing.PairingKey,
                DeviceId = state.DeviceId,
                LocalIp = state.LocalIp,
                Port = state.Port,
                Version = state.Version
            };

            var json = JsonSerializer.Serialize(payload);
            using var qrGenerator = new QRCodeGenerator();
            using var qrData = qrGenerator.CreateQrCode(json, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(qrData);
            var qrBase64 = Convert.ToBase64String(qr.GetGraphic(20,
                new byte[] { 255, 255, 255, 255 }, new byte[] { 30, 30, 30, 255 }));
            var safeJson = System.Net.WebUtility.HtmlEncode(json);

            var pairingTemplatePath = Path.Combine(AppContext.BaseDirectory, "Html", "pairing.html");
            var html = await File.ReadAllTextAsync(pairingTemplatePath);
            html = html.Replace("__QR_BASE64__", qrBase64, StringComparison.Ordinal)
                .Replace("__PAIRING_JSON__", safeJson, StringComparison.Ordinal)
                .Replace("__LOCAL_IP__", System.Net.WebUtility.HtmlEncode(state.LocalIp), StringComparison.Ordinal)
                .Replace("__PORT__", state.Port.ToString(), StringComparison.Ordinal)
                .Replace("__VERSION__", System.Net.WebUtility.HtmlEncode(state.Version), StringComparison.Ordinal);
            return Results.Content(html, "text/html; charset=utf-8");
        });
        app.MapGet("/api/system/ready", (ServerStateService serverState) =>
        {
            var state = serverState.Get();

            return Results.Ok(new
            {
                status = "ready",
                server = state.IsServerRunning,
                updated = state.LastUpdated
            });
        });
        app.MapGet("/api/setup/status", async (
            HttpContext context,
            SystemInfoService sysInfo,
            ServerStateService serverState,
            IPairingService pairingService,
            CancellationToken ct) =>
        {
            var pairing = await pairingService.GetPairingInfoAsync();
            var state = serverState.Get();

            var qrPayload = new PairingQrPayload
            {
                ServerId = pairing.ServerId,
                PairingKey = pairing.PairingKey,
                LocalIp = sysInfo.GetLocalIpAddress(),
                Port = sysInfo.Port,
            };
            return Results.Json(new
            {
                localIp = sysInfo.GetLocalIpAddress(),
                deviceId = sysInfo.DeviceId,
                port = sysInfo.Port,
                qrPayload
            }, embedJsonOptions, contentType: "application/json", statusCode: 200);
        });
        app.MapPost("/api/setup/license", async (
            HttpContext context,
            LicenseService licenseService,
            ServerStateService serverState,
            ServerStateFileService fileService,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);

            var data = JsonSerializer.Deserialize<JsonElement>(body);
            if (!data.TryGetProperty("licenseKey", out var keyProp))
            {
                var missing = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, context,
                    "The JSON body has no licenseKey field.");
                return Results.Json(missing, statusCode: missing.Error.Status);
            }

            var licenseKey = keyProp.GetString();
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                var empty = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, context,
                    "licenseKey was present but empty.");
                return Results.Json(empty, statusCode: empty.Error.Status);
            }

            var isValid = await licenseService.ValidateLicenseAsync(licenseKey);
            if (!isValid)
            {
                var invalid = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(403, context,
                    licenseService.IsLicensingServerConfigured
                        ? "NebulaLicensingServer rejected this key: it is unknown, revoked, expired, or bound to a different machine."
                        : "Licensing is not linked: Licensing:BaseUrl is empty in appsettings.json, so no key can be verified.",
                    licenseService.IsLicensingServerConfigured
                        ? "Check the key in the NebulaLicensingServer dashboard, confirm it is not revoked, and that it is bound to this machine. Keys are case sensitive."
                        : "Set Licensing:BaseUrl to your NebulaLicensingServer origin (for example https://localhost:7087) and restart the server.");
                return Results.Json(invalid, statusCode: invalid.Error.Status);
            }

            serverState.Update(state =>
            {
                state.IsLicensed = true;
                state.LicenseKey = licenseKey;
            });

            await fileService.SaveAsync(serverState.Get());

            return Results.Ok(new { success = true });
        });
    }

    private static IResult PageFile(string path)
    {
        var file = new FileInfo(path);
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
            $"\"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}\"");
        return Results.File(path, "text/html; charset=utf-8",
            lastModified: file.LastWriteTimeUtc, entityTag: etag);
    }
}
