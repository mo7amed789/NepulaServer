using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.Dashboard;
using NebulaServer.Services.Dashboard;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/dashboardapp")]
public class DashboardAppController : ControllerBase
{
    private readonly DashboardService _dashboard;

    public DashboardAppController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet]
    // 1. تم إزالة async و Task لأن الدالة أصبحت متزامنة
    public ActionResult<DashboardResponse> Get()
    {
        // 2. استدعاء GetDashboard() مباشرة بدون await
        return Ok(_dashboard.GetDashboard());
    }

}