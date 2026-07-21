using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Licensing;
using NebulaServer.Services;
using NebulaServer.Services.Ngrok;

namespace NebulaServer.Services.Licensing;

public sealed class LicenseShutdownCoordinator
{
    private readonly JobQueueManager _jobQueueManager;
    private readonly PythonProcessService _pythonProcessService;
    private readonly StreamingTransferService _streamingTransferService;
    private readonly INgrokManager _ngrokManager;
    private readonly ILogger<LicenseShutdownCoordinator> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public LicenseShutdownCoordinator(
        JobQueueManager jobQueueManager,
        PythonProcessService pythonProcessService,
        StreamingTransferService streamingTransferService,
        INgrokManager ngrokManager,
        IHostApplicationLifetime lifetime,
        ILogger<LicenseShutdownCoordinator> logger)
    {
        _jobQueueManager = jobQueueManager;
        _pythonProcessService = pythonProcessService;
        _streamingTransferService = streamingTransferService;
        _ngrokManager = ngrokManager;
        _lifetime = lifetime;
        _logger = logger;
    }

    public bool ShouldTerminate(LicenseState state) =>
        state is LicenseState.Revoked
            or LicenseState.Expired
            or LicenseState.MachineMismatch
            or LicenseState.ValidationFailed
            or LicenseState.InvalidSignature
            or LicenseState.TamperedLicense
            or LicenseState.NetworkFailure
            or LicenseState.Timeout
            or LicenseState.ServerUnavailable;

    public async Task TerminateAsync(LicenseState state, CancellationToken cancellationToken = default)
    {
        if (!ShouldTerminate(state))
        {
            return;
        }

        _logger.LogCritical("Stopping licensed services due to terminal license state {State}.", state);

        _jobQueueManager.CancelAllJobs();
        await _pythonProcessService.StopAsync(cancellationToken).ConfigureAwait(false);
        await _streamingTransferService.StopAsync(cancellationToken).ConfigureAwait(false);
        await _ngrokManager.StopAsync().ConfigureAwait(false);

        _lifetime.StopApplication();
    }
}
