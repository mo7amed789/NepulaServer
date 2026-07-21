using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.Pairing;
using NebulaServer.Services.Ngrok;
using NebulaServer.Services.Pairing;
using QRCoder;
using System.Text.Json;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly ServerStateService _serverState;
    private readonly IPairingService _pairingService;
    private readonly PublicUrlProvider _publicUrlProvider;

    public DashboardController(
        ServerStateService serverState,
        IPairingService pairingService,
        PublicUrlProvider publicUrlProvider)
    {
        _serverState = serverState;
        _pairingService = pairingService;
        _publicUrlProvider = publicUrlProvider;
    }

    [HttpGet("qr")]
    public async Task<IActionResult> GetQrCode()
    {
        if (!_publicUrlProvider.IsReady)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Ngrok not ready");

        var state = _serverState.Get();
        var publicUrl = _publicUrlProvider.Get();

        var pairing = await _pairingService.GetPairingInfoAsync();

        var payload = new PairingPayload
        {
            ServerId = pairing.ServerId,
            PairingKey = pairing.PairingKey,

            DeviceId = state.DeviceId,
            LocalIp = state.LocalIp,
            Port = state.Port,

            PublicUrl = publicUrl,

            Version = state.Version,

            IsNgrokRunning = state.IsNgrokRunning
        };

        string jsonPayload = JsonSerializer.Serialize(payload);

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(
            jsonPayload,
            QRCodeGenerator.ECCLevel.Q);

        using var qrCode = new PngByteQRCode(qrCodeData);

        byte[] image = qrCode.GetGraphic(
            20,
            new byte[] { 255, 255, 255, 255 },
            new byte[] { 30, 30, 30, 255 });

        return File(image, "image/png");
    }
}
