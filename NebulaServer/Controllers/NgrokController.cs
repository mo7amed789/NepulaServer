using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.Ngrok;
using NebulaServer.Services.Ngrok;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/ngrok")]
public class NgrokController : ControllerBase
{
    private readonly INgrokManager _ngrokManager;
    private readonly PublicUrlProvider _publicUrlProvider;

    public NgrokController(INgrokManager ngrokManager, PublicUrlProvider publicUrlProvider)
    {
        _ngrokManager = ngrokManager;
        _publicUrlProvider = publicUrlProvider;
    }

    [HttpPost("configure")]
    public async Task<IActionResult> Configure([FromBody] ConfigureNgrokRequest request)
    {
        var success = await _ngrokManager.ConfigureAsync(request.Authtoken);

        if (!success)
            return BadRequest(new
            {
                success = false,
                message = "Failed to configure ngrok."
            });

        return Ok(new
        {
            success = true,
            message = "Ngrok configured successfully."
        });
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        var success = await _ngrokManager.StartAsync();

        if (!success)
            return BadRequest(new
            {
                success = false
            });

        return Ok(new
        {
            success = true
        });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await _ngrokManager.StopAsync();

        return Ok(new
        {
            success = true
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        return Ok(await _ngrokManager.GetStatusAsync());
    }

    // تم تغيير اسم الدالة لتفادي التعارض مع ControllerBase.Url
    // المسار [HttpGet("url")] يضمن بقاء الـ API كما هو دون كسر الاتصال
    [HttpGet("url")]
    public IActionResult GetPublicUrl()
    {
        var url = _publicUrlProvider.Get();

        if (string.IsNullOrWhiteSpace(url))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Ngrok not ready");

        return Ok(new
        {
            publicUrl = url
        });
    }
}
