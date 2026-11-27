using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;

namespace NebulaServer.Services.Preview;

/// <summary>
/// Extracts and caches media thumbnails, titles, and available quality options via yt-dlp with an HTML fallback.
/// </summary>
public sealed class MediaPreviewService
{
    private static readonly ConcurrentDictionary<string, string> ThumbnailCache = new();
    private static readonly ConcurrentDictionary<string, PreviewData> PreviewCache = new();
    private static readonly string[] ProxyEnvVars =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"
    };

    private readonly ILogger _logger;

    public MediaPreviewService(ILogger<MediaPreviewService> logger)
        : this((ILogger)logger)
    {
    }

    public MediaPreviewService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<string?> ExtractThumbnailQuicklyAsync(string url, string? proxy = null)
    {
        var trimmedUrl = UrlNormalizer.NormalizeInputUrl(url);
        var cacheKey = GetPreviewCacheKey(trimmedUrl, proxy);

        if (ThumbnailCache.TryGetValue(cacheKey, out var cachedThumbnail))
            return cachedThumbnail;

        try
        {
            var startInfo = CreateYtDlpStartInfo(
                trimmedUrl,
                proxy,
                includeCookiesAndNode: false,
                extraArgs: new[] { "--no-check-certificates", "--no-config", "--flat-playlist", "--get-thumbnail" });

            var (exitCode, output, _) = await RunProcessAsync(startInfo);
            if (exitCode != 0) return null;

            var thumbnailUrl = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                ThumbnailCache[cacheKey] = thumbnailUrl;
                return thumbnailUrl;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<PreviewData> ExtractPreviewDataAsync(string url, string? proxy = null)
    {
        var trimmedUrl = UrlNormalizer.NormalizeInputUrl(url);
        var cacheKey = GetPreviewCacheKey(trimmedUrl, proxy);

        if (PreviewCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = new PreviewData();

        try
        {
            var startInfo = CreateYtDlpStartInfo(
                trimmedUrl,
                proxy,
                includeCookiesAndNode: true,
                extraArgs: new[]
                {
                    "--no-check-certificates", "--no-config", "--no-warnings",
                    "--skip-download", "--js-runtimes", "node",
                    "--remote-components", "ejs:github", "-J"
                });

            var (exitCode, output, stderr) = await RunProcessAsync(startInfo);
            if (exitCode != 0)
            {
                _logger.LogWarning(
                    "yt-dlp preview extraction failed for {Url} (exit {Code}): {Stderr}",
                    trimmedUrl, exitCode,
                    string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim());

                var fallback = await ExtractFallbackPreviewAsync(trimmedUrl, proxy);
                if (!string.IsNullOrWhiteSpace(fallback.ThumbnailUrl) || !string.IsNullOrWhiteSpace(fallback.Title))
                {
                    if (!string.IsNullOrWhiteSpace(fallback.ThumbnailUrl))
                        ThumbnailCache[cacheKey] = fallback.ThumbnailUrl;
                    PreviewCache[cacheKey] = fallback;
                    return fallback;
                }
                return result;
            }

            var jsonLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(jsonLine)) return result;

            result = ParseYtDlpPreviewJson(jsonLine);

            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
                ThumbnailCache[cacheKey] = result.ThumbnailUrl;
            PreviewCache[cacheKey] = result;

            return result;
        }
        catch
        {
            return result;
        }
    }

    private static ProcessStartInfo CreateYtDlpStartInfo(
        string url,
        string? proxy,
        bool includeCookiesAndNode,
        IEnumerable<string> extraArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RuntimePaths.ResolvePythonExecutable(),
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
        foreach (var arg in extraArgs)
            startInfo.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            startInfo.ArgumentList.Add("--proxy");
            startInfo.ArgumentList.Add(proxy.Trim());
        }

        if (includeCookiesAndNode)
        {
            var programDataCookies = Path.Combine(RuntimePaths.Data, "cookies.txt");
            var appBaseCookies = Path.Combine(RuntimePaths.Base, "cookies.txt");
            var cookiesFile = File.Exists(programDataCookies) ? programDataCookies
                : File.Exists(appBaseCookies) ? appBaseCookies
                : null;

            if (cookiesFile != null)
            {
                startInfo.ArgumentList.Add("--cookies");
                startInfo.ArgumentList.Add(cookiesFile);
            }

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
                    var existingPath = startInfo.Environment.TryGetValue("PATH", out var p)
                        ? p
                        : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    startInfo.Environment["PATH"] = nodeDir + Path.PathSeparator + existingPath;
                    break;
                }
            }
        }
        else
        {
            foreach (var proxyName in ProxyEnvVars)
                startInfo.Environment.Remove(proxyName);
        }

        startInfo.ArgumentList.Add(url);

        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        var bundledSitePackages = RuntimePaths.PythonSitePackages;
        if (!string.IsNullOrWhiteSpace(bundledSitePackages) && Directory.Exists(bundledSitePackages))
        {
            var existingPythonPath = startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath) ? pythonPath : string.Empty;
            startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                ? bundledSitePackages
                : bundledSitePackages + Path.PathSeparator + existingPythonPath;
        }

        return startInfo;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var outputStream = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream);
        var errorTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

        return (process.ExitCode, Encoding.UTF8.GetString(outputStream.ToArray()), await errorTask);
    }

    private static PreviewData ParseYtDlpPreviewJson(string jsonLine)
    {
        var result = new PreviewData();
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            result.Title = titleEl.GetString();

        if (root.TryGetProperty("thumbnail", out var thumbEl) && thumbEl.ValueKind == JsonValueKind.String)
        {
            result.ThumbnailUrl = thumbEl.GetString();
        }
        else if (root.TryGetProperty("thumbnails", out var thumbsArr) && thumbsArr.ValueKind == JsonValueKind.Array)
        {
            var lastThumb = thumbsArr.EnumerateArray().LastOrDefault();
            if (lastThumb.ValueKind != JsonValueKind.Undefined && lastThumb.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                result.ThumbnailUrl = urlEl.GetString();
        }

        double? durationSeconds = root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
            ? durEl.GetDouble()
            : null;

        var bestVideoByHeight = new Dictionary<int, (long? bytes, double? tbr)>();
        long? bestAudioBytes = null, bestAudioOnlyBytes = null;
        double? bestAudioTbr = null, bestAudioOnlyTbr = null;

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var fmt in formats.EnumerateArray())
            {
                var vcodec = fmt.TryGetProperty("vcodec", out var vcodecEl) && vcodecEl.ValueKind == JsonValueKind.String ? vcodecEl.GetString() : null;
                var acodec = fmt.TryGetProperty("acodec", out var acodecEl) && acodecEl.ValueKind == JsonValueKind.String ? acodecEl.GetString() : null;
                var isAudioOnly = vcodec == "none" && acodec != "none";

                long? filesize = null;
                if (fmt.TryGetProperty("filesize", out var fsEl) && fsEl.ValueKind == JsonValueKind.Number)
                    filesize = fsEl.GetInt64();
                else if (fmt.TryGetProperty("filesize_approx", out var fsaEl) && fsaEl.ValueKind == JsonValueKind.Number)
                    filesize = fsaEl.GetInt64();

                double? tbr = fmt.TryGetProperty("tbr", out var tbrEl) && tbrEl.ValueKind == JsonValueKind.Number
                    ? tbrEl.GetDouble()
                    : null;

                if (isAudioOnly)
                {
                    if (filesize.HasValue && (!bestAudioBytes.HasValue || filesize > bestAudioBytes)) { bestAudioBytes = filesize; bestAudioTbr = tbr; }
                    if (filesize.HasValue && (!bestAudioOnlyBytes.HasValue || filesize > bestAudioOnlyBytes)) { bestAudioOnlyBytes = filesize; bestAudioOnlyTbr = tbr; }
                    else if (!bestAudioOnlyBytes.HasValue && tbr.HasValue && (!bestAudioOnlyTbr.HasValue || tbr > bestAudioOnlyTbr)) bestAudioOnlyTbr = tbr;
                    continue;
                }

                if (!fmt.TryGetProperty("height", out var heightEl) || heightEl.ValueKind != JsonValueKind.Number) continue;
                var height = heightEl.GetInt32();

                if (!bestVideoByHeight.TryGetValue(height, out var existing) || (filesize.HasValue && (!existing.bytes.HasValue || filesize > existing.bytes)))
                    bestVideoByHeight[height] = (filesize, tbr);
            }
        }

        var qualities = new List<QualityOption>();
        foreach (var height in bestVideoByHeight.Keys.OrderByDescending(h => h))
        {
            var (videoBytes, videoTbr) = bestVideoByHeight[height];
            long? totalBytes = null;

            if (videoBytes.HasValue) totalBytes = videoBytes + (bestAudioBytes ?? 0);
            else if (videoTbr.HasValue && durationSeconds.HasValue) totalBytes = (long)((videoTbr.Value + (bestAudioTbr ?? 0)) * 1000 / 8 * durationSeconds.Value);

            qualities.Add(new QualityOption { Label = height.ToString(), FilesizeBytes = totalBytes, FilesizeDisplay = FormatBytes(totalBytes) });
        }

        long? audioTotalBytes = bestAudioOnlyBytes;
        if (!audioTotalBytes.HasValue && bestAudioOnlyTbr.HasValue && durationSeconds.HasValue)
            audioTotalBytes = (long)(bestAudioOnlyTbr.Value * 1000 / 8 * durationSeconds.Value);

        qualities.Add(new QualityOption { Label = "audio", FilesizeBytes = audioTotalBytes, FilesizeDisplay = FormatBytes(audioTotalBytes) });
        result.AvailableQualities = qualities;
        return result;
    }

    private async Task<PreviewData> ExtractFallbackPreviewAsync(string url, string? proxy = null)
    {
        var result = new PreviewData();
        try
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            if (!string.IsNullOrWhiteSpace(proxy) && ProxyAddress.TryNormalize(proxy, out var normalizedProxy))
            {
                handler.Proxy = ProxyAddress.CreateWebProxy(normalizedProxy!);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1");
            client.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            var html = await client.GetStringAsync(url);
            if (!string.IsNullOrWhiteSpace(html))
            {
                var titleMatch = Regex.Match(html, @"<meta\s+(?:property|name)=[""']og:title[""']\s+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!titleMatch.Success)
                    titleMatch = Regex.Match(html, @"content=[""']([^""']+)[""']\s+(?:property|name)=[""']og:title[""']", RegexOptions.IgnoreCase);
                if (!titleMatch.Success)
                    titleMatch = Regex.Match(html, @"<title>([^<]+)</title>", RegexOptions.IgnoreCase);

                if (titleMatch.Success)
                {
                    var rawTitle = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
                    if (rawTitle.Contains(" on Instagram: "))
                    {
                        var parts = rawTitle.Split(" on Instagram: ", 2);
                        rawTitle = parts.Length > 1 ? parts[1].Trim(' ', '"', '\'') : rawTitle;
                    }
                    result.Title = rawTitle;
                }

                var imgMatch = Regex.Match(html, @"<meta\s+(?:property|name)=[""']og:image(?::secure_url)?[""']\s+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!imgMatch.Success)
                    imgMatch = Regex.Match(html, @"content=[""']([^""']+)[""']\s+(?:property|name)=[""']og:image(?::secure_url)?[""']", RegexOptions.IgnoreCase);

                if (imgMatch.Success)
                    result.ThumbnailUrl = WebUtility.HtmlDecode(imgMatch.Groups[1].Value);

                if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
                {
                    result.AvailableQualities = new List<QualityOption>
                    {
                        new QualityOption { Label = "Original", FilesizeDisplay = "Original" },
                        new QualityOption { Label = "720", FilesizeDisplay = "" },
                        new QualityOption { Label = "audio", FilesizeDisplay = "" }
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallback preview extraction failed for {Url}", url);
        }
        return result;
    }

    private static string? FormatBytes(long? bytes)
    {
        if (!bytes.HasValue || bytes <= 0) return null;
        double size = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB" };
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1) { size /= 1024; unitIndex++; }
        return $"{size:0.#} {units[unitIndex]}";
    }

    private static string GetPreviewCacheKey(string url, string? proxy) =>
        $"{url}\n{(string.IsNullOrWhiteSpace(proxy) ? string.Empty : proxy.Trim())}";
}
