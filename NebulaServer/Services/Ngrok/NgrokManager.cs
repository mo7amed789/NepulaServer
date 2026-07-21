using System.Diagnostics;
using System.Text.Json;
using NebulaServer.Models;
using Microsoft.Extensions.Options;
using NebulaServer.Models.Configuration;

namespace NebulaServer.Services.Ngrok;

public class NgrokManager : INgrokManager, IDisposable
{
    private const int MaxStartAttempts = 5;
    private const int TunnelWaitAttempts = 30;

    // Minimum time that must pass between the *start* of two real start attempts.
    // No matter how often NgrokWatcher (or anyone else) calls StartAsync, we will
    // not actually try to spin up a new ngrok process more often than this. This
    // is the main fix for the connect/disconnect loop.
    private static readonly TimeSpan MinRetryInterval = TimeSpan.FromSeconds(30);

    private readonly string _ngrokPath;
    private readonly NgrokOptions _options;
    private readonly ServerStateService _serverState;
    private readonly PublicUrlProvider _publicUrlProvider;
    private readonly ILogger<NgrokManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    // The process *this instance* launched. Tracking it means Stop/monitoring
    // only ever touches the ngrok we own, instead of every ngrok.exe on the box.
    private Process? _ngrokProcess;

    // Guards + backoff bookkeeping.
    private volatile bool _starting;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    /// <summary>UTC time of the last failed start attempt (for diagnostics).</summary>
    public DateTime LastFailureUtc { get; private set; } = DateTime.MinValue;

    public NgrokManager(
        IOptions<NgrokOptions> options,
        ServerStateService serverState,
        PublicUrlProvider publicUrlProvider,
        ILogger<NgrokManager> logger)
    {
        _options = options.Value;
        _serverState = serverState;
        _publicUrlProvider = publicUrlProvider;
        _logger = logger;
        _ngrokPath = Path.Combine(AppContext.BaseDirectory, "Tools", "ngrok.exe");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    // -------------------------------------------------------------------------
    //  Initialize — called at startup; resumes existing tunnel or starts new one
    // -------------------------------------------------------------------------
    public async Task InitializeAsync()
    {
        try
        {
            string? url = await GetPublicUrlWithRetryAsync();

            if (!string.IsNullOrWhiteSpace(url))
            {
                Console.WriteLine($"Ngrok already running: {url}");

                _serverState.Update(state =>
                {
                    state.IsNgrokRunning = true;
                    state.IsConfigured = true;
                    state.PublicUrl = url;
                });
                _publicUrlProvider.Set(url);

                return;
            }

            await StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ngrok failed. Running in Local Mode.");

            _serverState.Update(state =>
            {
                state.IsNgrokRunning = false;
                state.IsConfigured = false;
                state.PublicUrl = null;
            });

            _publicUrlProvider.Clear();

            // لا ترمِ Exception
        }
    }

    // -------------------------------------------------------------------------
    //  Configure — saves the authtoken to ngrok's local config
    // -------------------------------------------------------------------------
    public async Task<bool> ConfigureAsync(string token)
    {
        if (!File.Exists(_ngrokPath))
            throw new FileNotFoundException("ngrok.exe not found.", _ngrokPath);

        var result = await ExecuteAsync($"config add-authtoken {token}");

        if (result.ExitCode != 0)
            throw new Exception(result.Error);

        return true;
    }

    // -------------------------------------------------------------------------
    //  Start — launches ngrok tunnel
    //
    //  This method is now safe to call as often as you like (e.g. every 5s from
    //  a watcher loop, and simultaneously from InitializeAsync/RecoveryBoot/a
    //  controller). It will only ever perform one real start attempt at a time,
    //  and will not retry a failed attempt more often than MinRetryInterval.
    // -------------------------------------------------------------------------
    public async Task<bool> StartAsync()
    {
        // Fast, non-blocking guard: a start is already running - don't queue up
        // behind it, just report that one is already in progress.
        if (_starting)
        {
            _logger.LogDebug("Ngrok start already in progress; skipping duplicate request.");
            return false;
        }

        // Backoff: never begin a new start attempt more often than
        // MinRetryInterval, regardless of who is calling. This is what stops a
        // watcher polling every few seconds from hammering ngrok in a loop.
        var sinceLastAttempt = DateTime.UtcNow - _lastAttemptUtc;
        if (sinceLastAttempt < MinRetryInterval)
        {
            _logger.LogDebug(
                "Skipping ngrok start attempt; only {Elapsed:0.0}s since last attempt (minimum {Min}s).",
                sinceLastAttempt.TotalSeconds, MinRetryInterval.TotalSeconds);
            return false;
        }

        // Try to take the lock immediately. If someone else already holds it
        // (e.g. Stop, or another Start call that slipped in between the checks
        // above), don't block - just bail out instead of piling up waiters.
        if (!await _operationLock.WaitAsync(0))
            return false;

        _starting = true;
        _lastAttemptUtc = DateTime.UtcNow;

        try
        {
            if (!File.Exists(_ngrokPath))
                throw new FileNotFoundException("ngrok.exe not found.", _ngrokPath);

            var existingUrl = await GetPublicUrlWithRetryAsync();

            if (!string.IsNullOrWhiteSpace(existingUrl))
            {
                _serverState.Update(state =>
                {
                    state.IsNgrokRunning = true;
                    state.IsConfigured = true;
                    state.PublicUrl = existingUrl;
                });
                _publicUrlProvider.Set(existingUrl);

                return true;
            }

            // Fail fast if there's no network. Without this, a dropped internet
            // connection makes every attempt burn through all 5 retries (each
            // waiting up to 30s for the ngrok API) before giving up - which is
            // exactly what fed the connect/disconnect loop.
            if (!await HasInternetAsync())
            {
                LastFailureUtc = DateTime.UtcNow;
                _logger.LogWarning("No internet connectivity detected; skipping ngrok start attempt.");

                _serverState.Update(state =>
                {
                    state.IsNgrokRunning = false;
                    state.IsConfigured = false;
                    state.PublicUrl = null;
                });
                _publicUrlProvider.Clear();

                return false;
            }

            for (int attempt = 1; attempt <= MaxStartAttempts; attempt++)
            {
                // Make sure we don't leak a previous attempt's process before
                // starting a new one.
                KillOwnedProcess();

                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = _ngrokPath,
                    Arguments = $"http {_options.TunnelPort} --log=stdout",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }) ?? throw new Exception("Failed to start ngrok process.");

                _ngrokProcess = process;

                try
                {
                    await WaitForApiAsync(process);

                    var url = await WaitForPublicUrlAsync(process);

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        _serverState.Update(state =>
                        {
                            state.IsNgrokRunning = true;
                            state.IsConfigured = true;
                            state.PublicUrl = url;
                        });
                        _publicUrlProvider.Set(url);

                        return true;
                    }

                    throw new Exception("ngrok API became available, but no public tunnel URL was exposed.");
                }
                catch (Exception ex)
                {
                    var stdout = string.Empty;
                    var stderr = string.Empty;

                    try
                    {
                        stdout = await process.StandardOutput.ReadToEndAsync();
                        stderr = await process.StandardError.ReadToEndAsync();
                    }
                    catch
                    {
                        // ignore logging failures
                    }

                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch { /* ignore */ }
                    }

                    if (!string.IsNullOrWhiteSpace(stderr) || !string.IsNullOrWhiteSpace(stdout))
                    {
                        _logger.LogError(
                            "ngrok start failed. ExitCode={ExitCode}. StdErr={StdErr}. StdOut={StdOut}",
                            process.HasExited ? process.ExitCode : -1,
                            stderr.Trim(),
                            stdout.Trim());
                    }

                    if (attempt >= MaxStartAttempts)
                    {
                        LastFailureUtc = DateTime.UtcNow;
                        throw new Exception(
                            $"ngrok failed to start (attempt {attempt}/{MaxStartAttempts}). " +
                            $"Last error: {stderr.Trim()}",
                            ex);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            _serverState.Update(state =>
            {
                state.IsNgrokRunning = false;
                state.IsConfigured = false;
                state.PublicUrl = null;
            });
            _publicUrlProvider.Clear();

            throw new Exception("Ngrok failed to start or expose a public tunnel.");
        }
        catch
        {
            LastFailureUtc = DateTime.UtcNow;
            throw;
        }
        finally
        {
            _starting = false;
            _operationLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    //  Stop — kills the ngrok process this instance owns (not every ngrok.exe
    //  on the machine).
    // -------------------------------------------------------------------------
    public async Task StopAsync()
    {
        await _operationLock.WaitAsync();

        try
        {
            KillOwnedProcess();

            _serverState.Update(state =>
            {
                state.IsNgrokRunning = false;
                state.PublicUrl = null;
            });
            _publicUrlProvider.Clear();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    //  GetPublicUrl — reads the tunnel URL from ngrok's local API
    // -------------------------------------------------------------------------
    public async Task<string?> GetPublicUrlAsync()
    {
        try
        {
            string json = await _httpClient.GetStringAsync(
                $"http://127.0.0.1:{_options.ApiPort}/api/tunnels");

            using var doc = JsonDocument.Parse(json);

            foreach (var tunnel in doc.RootElement.GetProperty("tunnels").EnumerateArray())
            {
                string? url = tunnel.GetProperty("public_url").GetString();

                if (url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                    return url;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    //  GetStatus — returns full status snapshot
    // -------------------------------------------------------------------------
    public async Task<NgrokStatus> GetStatusAsync()
    {
        var publicUrl = _publicUrlProvider.Get();

        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            publicUrl = await GetPublicUrlAsync();

            if (!string.IsNullOrWhiteSpace(publicUrl))
                _publicUrlProvider.Set(publicUrl);
        }

        var status = new NgrokStatus
        {
            IsInstalled = File.Exists(_ngrokPath),
            IsRunning = IsNgrokRunning(),
            PublicUrl = publicUrl
        };
        status.IsConfigured = !string.IsNullOrWhiteSpace(status.PublicUrl);

        return status;
    }

    // -------------------------------------------------------------------------
    //  Dispose
    // -------------------------------------------------------------------------
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // =========================================================================
    //  PRIVATE HELPERS
    // =========================================================================

    private bool IsNgrokRunning()
    {
        // Prefer the process we actually own - exact, instead of guessing from
        // every process named "ngrok" on the machine.
        if (_ngrokProcess is { } owned)
        {
            try
            {
                return !owned.HasExited;
            }
            catch
            {
                return false;
            }
        }

        // Fallback for the case where we adopted an already-running tunnel at
        // startup (InitializeAsync) and never launched our own process for it.
        return Process.GetProcessesByName("ngrok").Any();
    }

    private void KillOwnedProcess()
    {
        if (_ngrokProcess is null)
            return;

        try
        {
            if (!_ngrokProcess.HasExited)
                _ngrokProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _ngrokProcess.Dispose();
            _ngrokProcess = null;
        }
    }

    private async Task<bool> HasInternetAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync("https://connectivitycheck.gstatic.com/generate_204");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Same as GetPublicUrlAsync, but retries a couple of times with a short
    /// delay first. Guards against the case where ngrok is genuinely running
    /// and about to expose a tunnel, but its local API hasn't answered yet -
    /// without this we could mistakenly conclude "not running" and launch a
    /// second, redundant ngrok process.
    /// </summary>
    private async Task<string?> GetPublicUrlWithRetryAsync(int attempts = 2, int delayMs = 500)
    {
        for (int i = 0; i < attempts; i++)
        {
            var url = await GetPublicUrlAsync();

            if (!string.IsNullOrWhiteSpace(url))
                return url;

            if (i < attempts - 1)
                await Task.Delay(delayMs);
        }

        return null;
    }

    private async Task WaitForApiAsync(Process process)
    {
        for (int i = 0; i < TunnelWaitAttempts; i++)
        {
            if (process.HasExited)
                throw new Exception($"ngrok exited early with code {process.ExitCode}.");

            try
            {
                var response = await _httpClient.GetAsync(
                    $"http://127.0.0.1:{_options.ApiPort}/api/tunnels");

                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // not ready yet
            }

            await Task.Delay(1000);
        }

        throw new Exception(
            $"ngrok API did not respond on port {_options.ApiPort} after 30s. " +
            "Make sure ngrok is authenticated and not already running on another port.");
    }

    private async Task<string?> WaitForPublicUrlAsync(Process process)
    {
        for (int i = 0; i < TunnelWaitAttempts; i++)
        {
            if (process.HasExited)
                throw new Exception($"ngrok exited early with code {process.ExitCode}.");

            var url = await GetPublicUrlAsync();

            if (!string.IsNullOrWhiteSpace(url))
                return url;

            await Task.Delay(1000);
        }

        return null;
    }

    private async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ngrokPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new Exception("Failed to start ngrok process.");

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode, output, error);
    }
}