using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NebulaServer.Helpers;
using NebulaServer.Services.Server;
using NebulaServer.Services.Core;
using NebulaServer.Services.Jobs;
using NebulaServer.Models.Pairing;
using NebulaServer.Services.Pairing;
using NebulaServer.Models.Dashboard;
using NebulaServer.Services;
using NebulaServer.Services.Dashboard;
using System;
using System.IO;
using System.Threading.Tasks;

namespace NebulaServer.Controllers;

[ApiController]
public sealed class SystemController : ControllerBase
{
    private static readonly DateTime _serverStartTime = DateTime.UtcNow;

    private readonly RecoveryStateService _recoveryState;
    private readonly JobStore _jobStore;
    private readonly ServerStateService _serverState;
    private readonly IPairingService _pairingService;
    private readonly DashboardService _dashboard;

    public SystemController(
        RecoveryStateService recoveryState,
        JobStore jobStore,
        ServerStateService serverState,
        IPairingService pairingService,
        DashboardService dashboard)
    {
        _recoveryState = recoveryState;
        _jobStore = jobStore;
        _serverState = serverState;
        _pairingService = pairingService;
        _dashboard = dashboard;
    }

    [HttpGet("api/system/recovery")]
    public IActionResult GetRecoveryStatus()
    {
        var recovery = _recoveryState.Get();
        var server = _serverState.Get();
        var unfinished = _jobStore.GetUnfinished();

        return Ok(new
        {
            recovery,
            db = new
            {
                path = Path.Combine(RuntimePaths.Data, "jobs.db"),
                exists = System.IO.File.Exists(Path.Combine(RuntimePaths.Data, "jobs.db")),
                unfinishedJobs = unfinished.Count
            },
            network = new
            {
                publicUrl = "",
                publicUrlReady = false
            }
        });
    }

    [HttpGet("api/health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealthStatus()
    {
        return Ok(new
        {
            status = "online",
            version = "1.0.0",
            uptime = (DateTime.UtcNow - _serverStartTime).ToString(@"dd\.hh\:mm\:ss"),
            timestamp = DateTime.UtcNow
        });
    }

    [HttpGet("api/server/state")]
    public IActionResult GetState()
    {
        return Ok(_serverState.Get());
    }

    [HttpGet("api/server/health")]
    public IActionResult Health()
    {
        var state = _serverState.Get();
        return Ok(new
        {
            status = "Healthy",
            server = state.IsServerRunning,
            python = state.IsPythonReady,
            updated = state.LastUpdated
        });
    }

    [HttpGet("api/pairing")]
    public async Task<ActionResult<PairingResponse>> GetPairing()
    {
        var pairing = await _pairingService.GetPairingInfoAsync();
        var state = _serverState.Get();

        return Ok(new PairingResponse
        {
            ServerId = pairing.ServerId,
            PairingKey = pairing.PairingKey,
            LocalIp = state.LocalIp,
            Port = state.Port
        });
    }

    [HttpGet("api/dashboardapp")]
    public ActionResult<DashboardResponse> GetDashboard()
    {
        return Ok(_dashboard.GetDashboard());
    }

    [HttpGet("api/version")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetVersion()
    {
        return Ok(new
        {
            version = "1.4.2",
            apiSchema = 1,
            minClientVersion = "1.0.0"
        });
    }

    [HttpGet("api/system/startup")]
    public IActionResult GetStartupStatus()
    {
        return Ok(new
        {
            enabled = StartupHelper.IsStartupEnabled()
        });
    }

    public sealed class StartupRequest
    {
        public bool Enabled { get; set; }
    }

    [HttpPost("api/system/startup")]
    public IActionResult SetStartupStatus([FromBody] StartupRequest request)
    {
        var success = StartupHelper.SetStartup(request.Enabled);
        return Ok(new
        {
            success,
            enabled = StartupHelper.IsStartupEnabled()
        });
    }

    [HttpGet("api/system/show")]
    public IActionResult ShowWindow()
    {
        NebulaServer.Desktop.DesktopApplication.RequestShowWindow();
        return Ok(new { success = true, message = "Window show requested" });
    }

    [HttpGet("api/system/cookies")]
    public IActionResult GetCookieStatus()
    {
        var status = YouTubeCookieService.GetStatus();
        return Ok(new
        {
            exists = status.Exists,
            count = status.Count,
            isLoggedIn = status.IsLoggedIn,
            lastModified = status.LastModified,
            path = status.Path
        });
    }

    [HttpPost("api/system/cookies/sync")]
    public IActionResult RequestCookieSync()
    {
        NebulaServer.Desktop.DesktopApplication.RequestOpenYouTubeLogin();
        return Ok(new { success = true, message = "YouTube login window requested" });
    }

    public sealed class CookieContentRequest
    {
        public string? Content { get; set; }
    }

    [HttpPost("api/system/cookies/text")]
    public async Task<IActionResult> SaveCookieContent([FromBody] CookieContentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Content))
            return BadRequest(new { success = false, message = "Cookie content cannot be empty." });

        await YouTubeCookieService.SaveNetscapeCookiesAsync(request.Content);
        var status = YouTubeCookieService.GetStatus();
        return Ok(new
        {
            success = true,
            status = new
            {
                exists = status.Exists,
                count = status.Count,
                isLoggedIn = status.IsLoggedIn,
                lastModified = status.LastModified
            }
        });
    }

    [HttpDelete("api/system/cookies")]
    public IActionResult DeleteCookies()
    {
        YouTubeCookieService.DeleteCookies();
        return Ok(new { success = true });
    }
}
