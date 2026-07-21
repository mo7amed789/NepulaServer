using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using NebulaServer.Helpers;

namespace NebulaServer.Services;

public class PythonProcessService
{
    private readonly ILogger<PythonProcessService> _logger;
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    public PythonProcessService(ILogger<PythonProcessService> logger)
    {
        _logger = logger;
    }

    public Process StartProcess(string jsonFilePath)
    {
        var baseDir = AppContext.BaseDirectory;
        var pythonExe = ResolvePythonExecutable(baseDir);
        var enginePath = ResolveEnginePath(baseDir);
        var sitePackages = ResolveSitePackages(baseDir);

        _logger.LogInformation("========== Python Runtime ==========");
        _logger.LogInformation("Base Directory : {BaseDir}", baseDir);
        _logger.LogInformation("Python         : {Python}", pythonExe);
        _logger.LogInformation("Engine         : {Engine}", enginePath);
        _logger.LogInformation("Python Exists  : {PE}", File.Exists(pythonExe));
        _logger.LogInformation("Engine Exists  : {EE}", File.Exists(enginePath));

        if (!string.IsNullOrWhiteSpace(sitePackages))
            _logger.LogInformation("PYTHONPATH     : {SitePackages}", sitePackages);

        _logger.LogInformation("====================================");

        if (!File.Exists(pythonExe))
            throw new FileNotFoundException(
                $"Python not found.\n" +
                $"Checked: {pythonExe}\n" +
                $"Place python.exe in: {Path.Combine(baseDir, "Python")}");

        if (!File.Exists(enginePath))
            throw new FileNotFoundException(
                $"Engine script not found.\n" +
                $"Expected at: {enginePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = baseDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Support Windows py.exe launcher
        if (Path.GetFileName(pythonExe).Equals("py.exe", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add("-3");

        startInfo.ArgumentList.Add(enginePath);
        startInfo.ArgumentList.Add(jsonFilePath);

        // ── Python environment ────────────────────────────────────────────────
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        // ── PYTHONPATH: prepend bundled site-packages if present ──────────────
        if (!string.IsNullOrWhiteSpace(sitePackages) && Directory.Exists(sitePackages))
        {
            var existing = startInfo.Environment.TryGetValue("PYTHONPATH", out var v)
                ? v ?? string.Empty
                : string.Empty;

            startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                ? sitePackages
                : sitePackages + Path.PathSeparator + existing;
        }

        _logger.LogInformation("Starting Python engine: {Engine}", enginePath);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Python process.");
        }

        _activeProcesses[process.Id] = process;

        process.Exited += (_, _) =>
        {
            _activeProcesses.TryRemove(process.Id, out _);
        };

        return process;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var process in _activeProcesses.Values.ToArray())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort
            }
        }

        _activeProcesses.Clear();
        return Task.CompletedTask;
    }

    // =========================================================================
    // Path resolution helpers
    // =========================================================================

    /// <summary>
    /// Resolve Python executable with the following priority:
    ///   1. Embedded  → {baseDir}\Python\python.exe          (shipped with app)
    ///   2. System    → first python / python3 on PATH
    /// </summary>
    private string ResolvePythonExecutable(string baseDir)
    {
        // 1. Embedded Python (preferred — portable, version-controlled)
        var embedded = Path.Combine(baseDir, "Python", "python.exe");
        if (File.Exists(embedded))
        {
            _logger.LogInformation("Python source : embedded");
            return embedded;
        }

        // 2. System Python
        var candidates = new[] { "python.exe", "python3.exe", "py.exe" };
        foreach (var candidate in candidates)
        {
            var found = FindOnPath(candidate);
            if (found is not null)
            {
                _logger.LogWarning(
                    "Embedded Python not found at {Embedded}. Falling back to system: {Found}",
                    embedded, found);
                return found;
            }
        }

        // Return the expected embedded path so the error message is meaningful
        return embedded;
    }

    /// <summary>
    /// Resolve nebula_engine.py with the following priority:
    ///   1. {baseDir}\Engine\nebula_engine.py   (normal build/publish output)
    ///   2. {baseDir}\..\Engine\nebula_engine.py (one level up, dev convenience)
    /// </summary>
    private string ResolveEnginePath(string baseDir)
    {
        // Publish location
        var primary = Path.GetFullPath(
            Path.Combine(baseDir, "Python", "run", "eng", "engine.py"));

        if (File.Exists(primary))
            return primary;

        // Development fallback
        var devFallback = Path.GetFullPath(
            Path.Combine(baseDir, "..", "..", "..", "..", "Python", "run", "eng", "engine.py"));

        if (File.Exists(devFallback))
        {
            _logger.LogWarning(
                "Engine not found in output dir. Using dev source: {Path}",
                devFallback);

            return devFallback;
        }

        return primary;
    }

    /// <summary>
    /// Resolve bundled site-packages directory (optional).
    /// Returns null if not found — system Python's own site-packages will be used.
    /// </summary>
    private string? ResolveSitePackages(string baseDir)
    {
        var path = Path.Combine(baseDir, "Python", "Lib", "site-packages");
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Search for an executable on the system PATH.
    /// </summary>
    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // Skip invalid PATH entries
            }
        }
        return null;
    }
}
