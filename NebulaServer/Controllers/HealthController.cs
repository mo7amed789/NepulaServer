using Microsoft.AspNetCore.Mvc;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private static readonly DateTime _serverStartTime = DateTime.UtcNow;

    /// <summary>
    /// نقطة فحص خفيفة جداً للتحقق من حالة اتصال الخادم
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealthStatus()
    {
        // إرجاع كائن بسيط وسريع جداً لتقليل استهلاك الموارد
        return Ok(new
        {
            status = "online",
            version = "1.0.0", // يمكنك ربطها بإصدار التطبيق الفعلي
            uptime = (DateTime.UtcNow - _serverStartTime).ToString(@"dd\.hh\:mm\:ss"),
            timestamp = DateTime.UtcNow
        });
    }
}