using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;
using NebulaServer.Services.Server;

namespace NebulaServer.Services.Server;

public sealed class ServerInfoHostedService : BackgroundService
{
    private readonly SystemInfoService _systemInfo;
    private readonly ServerStateService _serverState;
    private readonly ILogger<ServerInfoHostedService> _logger;

    private bool _pythonReady;
    private string _pythonVersion = string.Empty;

    public ServerInfoHostedService(
        SystemInfoService systemInfo,
        ServerStateService serverState,
        ILogger<ServerInfoHostedService> logger)
    {
        _systemInfo = systemInfo;
        _serverState = serverState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========== Server Information Service ==========");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_pythonReady)
                {
                    var probe = ProbePythonRuntime();
                    if (probe.Ready)
                    {
                        _pythonReady = true;
                        _pythonVersion = probe.Version;
                        _logger.LogInformation("Python engine ready (Python {Version}, {Engine})", probe.Version, RuntimePaths.Engine);
                    }
                }

                _serverState.Update(state =>
                {
                    state.DeviceId = _systemInfo.DeviceId;
                    state.LocalIp = _systemInfo.GetLocalIpAddress();
                    state.Port = _systemInfo.Port;
                    state.IsServerRunning = true;
                    state.Version = _systemInfo.Version;
                    state.IsPythonReady = _pythonReady;
                    state.PythonVersion = _pythonVersion;
                });

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Server Information Service stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server Information Service crashed.");
        }
    }

    /// <summary>
    /// Verifies the media engine can actually run: a resolvable Python
    /// interpreter plus the nebula engine script. Runs until it succeeds so a
    /// runtime installed later is picked up.
    /// </summary>
    private static (bool Ready, string Version) ProbePythonRuntime()
    {
        try
        {
            var pythonExe = RuntimePaths.ResolvePythonExecutable();
            if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
                return (false, string.Empty);

            if (!File.Exists(RuntimePaths.Engine))
                return (false, string.Empty);

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-c \"import sys; print(sys.version.split()[0])\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, string.Empty);

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            return process.HasExited && process.ExitCode == 0 && output.Length > 0
                ? (true, output)
                : (false, string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _serverState.Update(state =>
        {
            state.IsServerRunning = false;
        });

        await base.StopAsync(cancellationToken);
    }
}
