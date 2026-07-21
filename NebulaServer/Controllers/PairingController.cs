using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.Licensing;
using NebulaServer.Models.Pairing;
using NebulaServer.Services.Licensing;
using NebulaServer.Services.Ngrok;
using NebulaServer.Services.Pairing;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/pairing")]
public sealed class PairingController : ControllerBase
{
    private readonly IPairingService _pairingService;
    private readonly ServerStateService _serverState;
    private readonly PublicUrlProvider _publicUrlProvider;
    private readonly ILicenseManager _licenseManager;

    public PairingController(
    IPairingService pairingService,
    ServerStateService serverState,
    PublicUrlProvider publicUrlProvider,
    ILicenseManager licenseManager)
    {
        _pairingService = pairingService;
        _serverState = serverState;
        _publicUrlProvider = publicUrlProvider;
        _licenseManager = licenseManager;
    }

    [HttpGet]
    public async Task<ActionResult<PairingResponse>> GetPairing()
    {
        var pairing = await _pairingService.GetPairingInfoAsync();

        var state = _serverState.Get();

        return Ok(new PairingResponse
        {
            ServerId = pairing.ServerId,

            DeviceId = state.DeviceId,
            LocalIp = state.LocalIp,
            Port = state.Port,

            PublicUrl = _publicUrlProvider.Get() ?? state.PublicUrl,

            Version = state.Version,

            IsNgrokRunning = state.IsNgrokRunning
        });

    }
    [HttpPost("license/activate")]
    public async Task<IActionResult> ActivateLicense(
    [FromBody] ActivateLicenseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LicenseKey))
        {
            return BadRequest(new
            {
                success = false,
                message = "License key is required."
            });
        }

        var result = await _licenseManager.ActivateAsync(request.LicenseKey);

        if (!result.Success)
        {
            return BadRequest(new
            {
                success = false,
                message = result.Message,
                status = result.Status.ToString()
            });
        }

        return Ok(new
        {
            success = true,
            message = "License activated successfully."
        });
    }
}
