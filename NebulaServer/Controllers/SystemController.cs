using Microsoft.AspNetCore.Mvc;
using NebulaServer.Helpers;
using NebulaServer.Services.Ngrok;
using NebulaServer.Services.Core;
using NebulaServer.Services.Jobs;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    private readonly RecoveryStateService _recoveryState;
    private readonly JobStore _jobStore;
    private readonly PublicUrlProvider _publicUrlProvider;
    private readonly ServerStateService _serverState;

    public SystemController(
        RecoveryStateService recoveryState,
        JobStore jobStore,
        PublicUrlProvider publicUrlProvider,
        ServerStateService serverState)
    {
        _recoveryState = recoveryState;
        _jobStore = jobStore;
        _publicUrlProvider = publicUrlProvider;
        _serverState = serverState;
    }

    [HttpGet("recovery")]
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
                publicUrl = _publicUrlProvider.Get() ?? server.PublicUrl,
                publicUrlReady = _publicUrlProvider.IsReady
            }
        });
    }
}
