using System;
using System.Diagnostics;
using System.IO;

namespace NebulaServer.Helpers;

public static class RuntimePaths
{
    private static readonly Lazy<string> PythonExecutableCache = new(ResolvePythonExecutableCore);
    private static readonly Lazy<string?> PythonSitePackagesCache = new(ResolvePythonSitePackagesCore);

    // =========================
    // Program Files
    // =========================

    public static string Base => AppContext.BaseDirectory;

    public static string PythonExe =>
        Path.Combine(Base, "Python", "python.exe");

    public static string? PythonSitePackages => PythonSitePackagesCache.Value;

    public static string Engine =>
        ResolveEnginePath();

    public static string FFmpeg =>
        Path.Combine(Base, "ffmpeg.exe");

    public static string FFprobe =>
        Path.Combine(Base, "ffprobe.exe");

    public static string Models =>
        Path.Combine(Base, "Engine", "Models");

    // =========================
    // ProgramData
    // =========================

    public static string Data =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Tanzeel");

    public static string Downloads =>
        Path.Combine(Data, "Downloads");

    public static string Jobs =>
        Path.Combine(Data, "Jobs");

    public static string Logs =>
        Path.Combine(Data, "Logs");

    public static string Temp =>
        Path.Combine(Data, "Temp");

    public static string Archives =>
        Path.Combine(Data, "Archives");

    public static string ResolvePythonExecutable() => PythonExecutableCache.Value;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Archives);
    }

    private static string ResolvePythonExecutableCore()
    {
        // 1. Embedded Python runtime (preferred — shipped alongside binary)
        if (File.Exists(PythonExe))
            return PythonExe;

        // 2. Development source Python runtime (for development convenience)
        var devPython = Path.Combine(GetDevelopmentRoot(), "Python", "python.exe");
        if (File.Exists(devPython))
            return devPython;

        // 3. System PATH
        var onPath = FindOnPath("python.exe") ?? FindOnPath("python3.exe");
        if (onPath is not null)
            return onPath;

        // 4. Well-known standard Python installations (Python 3.11 preferred)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var searchRoots = new[] { localAppData, programFiles };
        var subPaths = new[]
        {
            @"Programs\Python\Python311\python.exe",
            @"Python311\python.exe",
            @"Programs\Python\Python312\python.exe",
            @"Python312\python.exe",
            @"Programs\Python\Python310\python.exe",
            @"Python310\python.exe",
            @"Programs\Python\Python39\python.exe",
            @"Python39\python.exe",
            @"Programs\Python\Python38\python.exe",
            @"Python38\python.exe",
            @"Programs\Python\Python313\python.exe",
            @"Python313\python.exe",
        };

        foreach (var root in searchRoots)
        foreach (var sub in subPaths)
        {
            var candidate = Path.Combine(root, sub);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Python executable not found.\n" +
            $"Checked embedded path : {PythonExe}\n" +
            $"Checked dev path      : {devPython}\n" +
            "Checked system PATH   : python.exe / python3.exe\n" +
            "Checked well-known    : %LocalAppData%\\Programs\\Python\\Python3xx\\\n\n" +
            $"Fix: place Python runtime in '{PythonExe}' relative to the server binary, " +
            "or install Python 3.11+ and add it to the system PATH.");
    }

    private static string? ResolvePythonSitePackagesCore()
    {
        var devCandidate = Path.Combine(GetDevelopmentRoot(), "Python", "Lib", "site-packages");
        if (Directory.Exists(devCandidate))
            return devCandidate;

        var outputCandidate = Path.Combine(Base, "Python", "Lib", "site-packages");
        return Directory.Exists(outputCandidate) ? outputCandidate : null;
    }

    private static string GetDevelopmentRoot() =>
        Path.GetFullPath(Path.Combine(Base, "..", "..", "..", ".."));

    private static string ResolveEnginePath()
    {
        var devPythonCandidate = Path.Combine(GetDevelopmentRoot(), "Python", "nebula_engine.py");
        if (File.Exists(devPythonCandidate))
            return devPythonCandidate;

        var devEngineCandidate = Path.Combine(GetDevelopmentRoot(), "Engine", "nebula_engine.py");
        if (File.Exists(devEngineCandidate))
            return devEngineCandidate;

        var basePythonCandidate = Path.Combine(Base, "Python", "nebula_engine.py");
        if (File.Exists(basePythonCandidate))
            return basePythonCandidate;

        return Path.Combine(Base, "Engine", "nebula_engine.py");
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = exe,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(info)!;
            var result = proc.StandardOutput.ReadLine()?.Trim();
            proc.WaitForExit();

            return (proc.ExitCode == 0 && File.Exists(result)) ? result : null;
        }
        catch
        {
            return null;
        }
    }
}
