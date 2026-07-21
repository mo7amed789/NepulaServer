using Microsoft.AspNetCore.Mvc;
using NebulaServer.Services.Ngrok;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/server")]
public class ServerController : ControllerBase
{
    private readonly ServerStateService _serverState;

    public ServerController(ServerStateService serverState)
    {
        _serverState = serverState;
    }

    /// <summary>
    /// Returns the current Nebula server state.
    /// </summary>
    [HttpGet("state")]
    [ProducesResponseType(typeof(ServerState), StatusCodes.Status200OK)]
    public IActionResult GetState()
    {
        return Ok(_serverState.Get());
    }

    /// <summary>
    /// Simple health endpoint.
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        var state = _serverState.Get();

        return Ok(new
        {
            status = "Healthy",
            server = state.IsServerRunning,
            python = state.IsPythonReady,
            ngrok = state.IsNgrokRunning,
            updated = state.LastUpdated
        });
    }
}