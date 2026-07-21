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
            "Nebula Server");

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
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var searchRoots = new[] { localAppData, programFiles };
        var subPaths = new[]
        {
            @"Programs\Python\Python313\python.exe",
            @"Programs\Python\Python312\python.exe",
            @"Programs\Python\Python311\python.exe",
            @"Programs\Python\Python310\python.exe",
            @"Programs\Python\Python39\python.exe",
            @"Programs\Python\Python38\python.exe",
            @"Python313\python.exe",
            @"Python312\python.exe",
            @"Python311\python.exe",
            @"Python310\python.exe",
        };

        // Prefer a 3.13 interpreter first because the bundled Python packages in this repo
        // were built for cp313. Falling back to the embedded runtime only if needed.
        foreach (var root in searchRoots)
        foreach (var sub in subPaths)
        {
            if (!sub.Contains("Python313", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(root, sub);
            if (File.Exists(candidate))
                return candidate;
        }

        var devPython = Path.Combine(GetDevelopmentRoot(), "Python", "python.exe");
        if (File.Exists(devPython))
            return devPython;

        if (File.Exists(PythonExe))
            return PythonExe;

        var onPath = FindOnPath("python.exe") ?? FindOnPath("python3.exe");
        if (onPath is not null)
            return onPath;

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
            "or install Python 3.8+ and add it to the system PATH.");
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
        var devCandidate = Path.Combine(GetDevelopmentRoot(), "Engine", "nebula_engine.py");
        if (File.Exists(devCandidate))
            return devCandidate;

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
