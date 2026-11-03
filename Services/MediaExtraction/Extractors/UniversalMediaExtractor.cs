using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;
using NebulaServer.Models.Media;

namespace NebulaServer.Services.MediaExtraction.Extractors;

public class UniversalMediaExtractor : IMediaExtractor
{
    private readonly ILogger<UniversalMediaExtractor> _logger;

    public UniversalMediaExtractor(ILogger<UniversalMediaExtractor> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(Uri url)
    {
        return url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;
    }

    public async Task<MediaResult> ExtractAsync(Uri url, CancellationToken ct = default)
    {
        var trimmedUrl = url.ToString();
        var pythonExecutable = RuntimePaths.ResolvePythonExecutable();
        var bundledSitePackages = RuntimePaths.PythonSitePackages;

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RuntimePaths.Base
        };

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("yt_dlp");
        startInfo.ArgumentList.Add("--no-check-certificates");
        startInfo.ArgumentList.Add("--no-config");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--js-runtimes");
        startInfo.ArgumentList.Add("node");
        startInfo.ArgumentList.Add("--remote-components");
        startInfo.ArgumentList.Add("ejs:github");
        startInfo.ArgumentList.Add("-J");

        var programDataCookies = Path.Combine(RuntimePaths.Data, "cookies.txt");
        var appBaseCookies = Path.Combine(RuntimePaths.Base, "cookies.txt");

        if (File.Exists(programDataCookies))
        {
            startInfo.ArgumentList.Add("--cookies");
            startInfo.ArgumentList.Add(programDataCookies);
        }
        else if (File.Exists(appBaseCookies))
        {
            startInfo.ArgumentList.Add("--cookies");
            startInfo.ArgumentList.Add(appBaseCookies);
        }

        startInfo.ArgumentList.Add(trimmedUrl);

        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        // Node.js directory on PATH for modern yt-dlp EJS challenges
        var commonNodeDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"),
            Path.Combine(RuntimePaths.Base, "Node")
        };
        foreach (var nodeDir in commonNodeDirs)
        {
            if (File.Exists(Path.Combine(nodeDir, "node.exe")))
            {
                var existingPath = startInfo.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                startInfo.Environment["PATH"] = nodeDir + Path.PathSeparator + existingPath;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(bundledSitePackages) && Directory.Exists(bundledSitePackages))
        {
            var existingPythonPath = startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath) ? pythonPath : string.Empty;
            startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                ? bundledSitePackages
                : bundledSitePackages + Path.PathSeparator + existingPythonPath;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Python process for media extraction.");
            throw new MediaExtractionException(
                ExtractionErrorCode.ExtractionFailed,
                $"Failed to start extractor runtime: {ex.Message}", ex);
        }

        using var outputStream = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream, ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));

        var stderr = await errorTask;
        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "yt-dlp extraction failed for {Url} (exit {Code}): {Stderr}",
                trimmedUrl, process.ExitCode,
                string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim());

            var lowerErr = (stderr ?? string.Empty).ToLowerInvariant();
            if (lowerErr.Contains("sign in") || lowerErr.Contains("login") || lowerErr.Contains("confirm you’re not a bot"))
            {
                throw new MediaExtractionException(
                    ExtractionErrorCode.AuthenticationRequired,
                    "The media platform requires authentication or valid session cookies.");
            }

            if (lowerErr.Contains("video unavailable") || lowerErr.Contains("does not exist") || lowerErr.Contains("private video"))
            {
                throw new MediaExtractionException(
                    ExtractionErrorCode.MediaUnavailable,
                    "The requested media is unavailable, private, or has been deleted.");
            }

            throw new MediaExtractionException(
                ExtractionErrorCode.ExtractionFailed,
                $"Extraction failed: {(string.IsNullOrWhiteSpace(stderr) ? "Unknown error." : stderr.Trim())}");
        }

        var output = Encoding.UTF8.GetString(outputStream.ToArray());
        var jsonLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            throw new MediaExtractionException(
                ExtractionErrorCode.ExtractionFailed,
                "Extractor returned empty response.");
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "media" : "media";
            var extractor = root.TryGetProperty("extractor", out var extractorEl) ? extractorEl.GetString() ?? "Web" : "Web";

            string? thumbnail = null;
            if (root.TryGetProperty("thumbnail", out var thumbEl) && thumbEl.ValueKind == JsonValueKind.String)
            {
                thumbnail = thumbEl.GetString();
            }
            else if (root.TryGetProperty("thumbnails", out var thumbsArr) && thumbsArr.ValueKind == JsonValueKind.Array)
            {
                var lastThumb = thumbsArr.EnumerateArray().LastOrDefault();
                if (lastThumb.ValueKind != JsonValueKind.Undefined && lastThumb.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    thumbnail = urlEl.GetString();
                }
            }

            TimeSpan? duration = null;
            if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            {
                duration = TimeSpan.FromSeconds(durEl.GetDouble());
            }

            var formats = new List<MediaFormat>();
            if (root.TryGetProperty("formats", out var formatsEl) && formatsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var fmt in formatsEl.EnumerateArray())
                {
                    var formatUrl = fmt.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(formatUrl)) continue;

                    var formatId = fmt.TryGetProperty("format_id", out var fIdEl) ? fIdEl.GetString() ?? "0" : "0";
                    var ext = fmt.TryGetProperty("ext", out var extEl) ? extEl.GetString() ?? "mp4" : "mp4";

                    var vcodec = fmt.TryGetProperty("vcodec", out var vcEl) ? vcEl.GetString() : null;
                    var acodec = fmt.TryGetProperty("acodec", out var acEl) ? acEl.GetString() : null;

                    var hasVideo = !string.IsNullOrWhiteSpace(vcodec) && vcodec != "none";
                    var hasAudio = !string.IsNullOrWhiteSpace(acodec) && acodec != "none";

                    long? filesize = null;
                    if (fmt.TryGetProperty("filesize", out var fsEl) && fsEl.ValueKind == JsonValueKind.Number)
                        filesize = fsEl.GetInt64();
                    else if (fmt.TryGetProperty("filesize_approx", out var fsaEl) && fsaEl.ValueKind == JsonValueKind.Number)
                        filesize = fsaEl.GetInt64();

                    int? width = fmt.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetInt32() : null;
                    int? height = fmt.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number ? hEl.GetInt32() : null;

                    string? quality = null;
                    if (height.HasValue) quality = $"{height}p";
                    else if (fmt.TryGetProperty("format_note", out var fnEl)) quality = fnEl.GetString();

                    formats.Add(new MediaFormat
                    {
                        Id = formatId,
                        Extension = ext,
                        Url = formatUrl,
                        Quality = quality,
                        Size = filesize,
                        HasVideo = hasVideo,
                        HasAudio = hasAudio
                    });
                }
            }

            return new MediaResult
            {
                Id = id,
                Title = title,
                Thumbnail = thumbnail,
                Duration = duration,
                Platform = char.ToUpperInvariant(extractor[0]) + (extractor.Length > 1 ? extractor[1..] : string.Empty),
                Formats = formats
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse yt-dlp JSON output.");
            throw new MediaExtractionException(
                ExtractionErrorCode.ExtractionFailed,
                "Failed to parse media metadata from extractor.", ex);
        }
    }
}
