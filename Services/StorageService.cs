using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;

namespace NebulaServer.Services;

public class QualityOption
{
    public string Label { get; set; } = string.Empty;   // "1080", "720", "audio"
    public long? FilesizeBytes { get; set; }
    public string? FilesizeDisplay { get; set; }
}

public class PreviewData
{
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<QualityOption> AvailableQualities { get; set; } = new();
}

public class StorageService
{
    private readonly ILogger<StorageService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ThumbnailCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingPlaylistDeletion = new();
    private static readonly ConcurrentDictionary<string, PreviewData> PreviewCache = new();

    public static readonly TimeSpan DefaultPlaylistRetention = TimeSpan.FromSeconds(20);

    public string DownloadsPath { get; }
    public string JobsPath { get; }
    public string TempPath { get; }
    public string LogsPath { get; }
    public string ArchivesPath { get; }

    public StorageService(ILogger<StorageService> logger)
    {
        _logger = logger;

        DownloadsPath = RuntimePaths.Downloads;
        JobsPath = RuntimePaths.Jobs;
        TempPath = RuntimePaths.Temp;
        LogsPath = RuntimePaths.Logs;
        ArchivesPath = RuntimePaths.Archives;

        RuntimePaths.EnsureDirectories();
    }

    // =========================================================
    // URL Normalization & Validation
    // =========================================================
    public static string NormalizeInputUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        input = input.Trim();

        // Markdown link: [https://example.com](https://example.com)
        var markdownMatch = Regex.Match(
            input,
            @"\]\((https?://[^)]+)\)",
            RegexOptions.IgnoreCase);

        if (markdownMatch.Success)
            return markdownMatch.Groups[1].Value;

        // [https://example.com]
        var bracketMatch = Regex.Match(
            input,
            @"\[(https?://[^\]]+)\]",
            RegexOptions.IgnoreCase);

        if (bracketMatch.Success)
            return bracketMatch.Groups[1].Value;

        // Instagram URLs normalization:
        // 1. instagr.am -> www.instagram.com
        input = Regex.Replace(input, @"^https?://(?:www\.)?instagr\.am/", "https://www.instagram.com/", RegexOptions.IgnoreCase);

        // 2. /share/reel/ or /share/p/ or /share/tv/ -> /reel/ or /p/ or /tv/
        input = Regex.Replace(input, @"/share/(reels?|p|tv)/([^/?#&]+)", "/$1/$2", RegexOptions.IgnoreCase);

        // 3. /reels/ -> /reel/
        input = Regex.Replace(input, @"/reels/([^/?#&]+)", "/reel/$1", RegexOptions.IgnoreCase);

        // 4. Strip tracking parameters (?igsh=..., utm_...) from Instagram URLs
        if (input.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            var qIdx = input.IndexOf('?');
            if (qIdx > 0)
            {
                var query = input[(qIdx + 1)..];
                if (query.Contains("igsh=", StringComparison.OrdinalIgnoreCase) || 
                    query.Contains("utm_", StringComparison.OrdinalIgnoreCase))
                {
                    input = input[..qIdx];
                }
            }

            // Ensure trailing slash for post/reel IDs if missing
            if (Regex.IsMatch(input, @"/(?:p|tv|reel)/[^/?#]+$"))
            {
                input += "/";
            }
        }

        return input;
    }

    private static bool IsValidHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        url = url.Trim();

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    // =========================================================
    // Social Media Metadata Extraction Helpers
    // =========================================================
    public async Task<string?> ExtractSocialMediaImageUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        url = NormalizeInputUrl(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language",
            "en-US,en;q=0.9");

        // Load cookies
        var cookiesPath = Path.Combine(RuntimePaths.Data, "cookies.txt");
        if (File.Exists(cookiesPath))
        {
            var cookieHeader = BuildCookieHeaderFromNetscapeFile(cookiesPath, uri);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        // Download HTML
        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Social media metadata request returned {StatusCode} for {Url}", (int)response.StatusCode, url);
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        // 1. og:image
        var imageUrl = ExtractMetaImage(html, "property", "og:image");
        if (IsValidHttpUrl(imageUrl))
        {
            _logger.LogInformation("Found social media image through og:image: {ImageUrl}", imageUrl);
            return imageUrl;
        }

        // 2. twitter:image
        imageUrl = ExtractMetaImage(html, "name", "twitter:image");
        if (IsValidHttpUrl(imageUrl))
        {
            _logger.LogInformation("Found social media image through twitter:image: {ImageUrl}", imageUrl);
            return imageUrl;
        }

        // 3. Instagram embedded JSON
        imageUrl = ExtractDisplayUrl(html);
        if (IsValidHttpUrl(imageUrl))
        {
            _logger.LogInformation("Found social media image through display_url: {ImageUrl}", imageUrl);
            return imageUrl;
        }

        _logger.LogWarning("Could not find social media image metadata for {Url}", url);
        return null;
    }

    private static string? ExtractMetaImage(string html, string attribute, string value)
    {
        var escapedValue = Regex.Escape(value);
        var patterns = new[]
        {
            $@"<meta\s+[^>]*{attribute}\s*=\s*[""']{escapedValue}[""'][^>]*content\s*=\s*[""']([^""']+)[""']",
            $@"<meta\s+[^>]*content\s*=\s*[""']([^""']+)[""'][^>]*{attribute}\s*=\s*[""']{escapedValue}[""']"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) continue;

            var result = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            if (IsValidHttpUrl(result)) return result;
        }
        return null;
    }

    private static string? ExtractDisplayUrl(string html)
    {
        var patterns = new[]
        {
            @"""display_url""\s*:\s*""([^""]+)""",
            @"""displayUrl""\s*:\s*""([^""]+)"""
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var imageUrl = match.Groups[1].Value
                .Replace("\\/", "/")
                .Replace("\\u0026", "&");

            imageUrl = Regex.Unescape(imageUrl);
            if (IsValidHttpUrl(imageUrl)) return imageUrl;
        }
        return null;
    }

    private static string? BuildCookieHeaderFromNetscapeFile(string path, Uri targetUri)
    {
        var host = targetUri.Host.TrimStart('.').ToLowerInvariant();
        var cookies = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;

            var domain = parts[0].Trim().TrimStart('.').ToLowerInvariant();
            var name = parts[5].Trim();
            var value = parts[6].Trim();

            if (string.IsNullOrWhiteSpace(name)) continue;

            var domainMatches = host == domain || host.EndsWith("." + domain);
            if (!domainMatches) continue;

            cookies.Add($"{name}={value}");
        }

        return cookies.Count == 0 ? null : string.Join("; ", cookies);
    }

    // =========================================================
    // Core Storage & Thumbnail / Preview Methods
    // =========================================================
    public string GetJobJsonPath(string jobId) => Path.Combine(JobsPath, $"{jobId}.json");
    public string GetDownloadFilePath(string fileName) => Path.Combine(DownloadsPath, fileName);
    public string GetPlaylistDirectoryPath(string jobId) => Path.Combine(DownloadsPath, jobId);

    public async Task<FileLockLease> AcquireFileLockAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        var gate = _fileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new FileLockLease(normalizedPath, gate);
    }

    public IReadOnlyList<string> GetPlaylistFiles(string jobId)
    {
        var sourceDirectory = GetPlaylistDirectoryPath(jobId);
        if (!Directory.Exists(sourceDirectory)) return Array.Empty<string>();

        return Directory.EnumerateFiles(sourceDirectory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CreatePlaylistArchive(string jobId, string? archiveFileName)
    {
        var sourceDirectory = GetPlaylistDirectoryPath(jobId);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Playlist output directory was not found for job {jobId}.");

        var safeFileName = string.IsNullOrWhiteSpace(archiveFileName) ? $"{jobId}.zip" : archiveFileName;
        if (!safeFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) safeFileName += ".zip";

        var archivePath = Path.Combine(ArchivesPath, safeFileName);
        if (File.Exists(archivePath)) File.Delete(archivePath);

        ZipFile.CreateFromDirectory(sourceDirectory, archivePath, CompressionLevel.Fastest, false);
        _logger.LogInformation("Created playlist archive for job {JobId} at {ArchivePath}", jobId, archivePath);

        return archivePath;
    }

    public void SchedulePlaylistDeletion(string jobId, TimeSpan? delay = null)
    {
        var effectiveDelay = delay ?? DefaultPlaylistRetention;
        var dueAt = DateTime.UtcNow.Add(effectiveDelay);
        _pendingPlaylistDeletion[jobId] = dueAt;
        _logger.LogInformation("Scheduled fallback playlist cleanup for job {JobId} at {DueAt:u} (in {Delay})", jobId, dueAt, effectiveDelay);
    }

    public void DeletePlaylistAfterTransfer(string jobId)
    {
        var path = GetPlaylistDirectoryPath(jobId);
        var titleFilePath = Path.Combine(DownloadsPath, $"{jobId}_title.txt");
        var archiveFilePath = Path.Combine(ArchivesPath, $"{jobId}.zip");

        try
        {
            if (Directory.Exists(path))
            {
                if (DirectoryContainsLockedFiles(path))
                {
                    _logger.LogWarning("Playlist folder for job {JobId} still has locked files right after transfer; leaving it for the scheduled fallback cleanup at {Path}", jobId, path);
                    return;
                }
                Directory.Delete(path, true);
                _logger.LogInformation("Deleted playlist folder for job {JobId} at {Path}", jobId, path);
            }

            if (File.Exists(titleFilePath))
            {
                try { File.Delete(titleFilePath); } catch { _logger.LogWarning("Could not delete title file {File}", titleFilePath); }
            }

            if (File.Exists(archiveFilePath))
            {
                try { File.Delete(archiveFilePath); } catch { _logger.LogWarning("Could not delete archive file {File}", archiveFilePath); }
            }

            _pendingPlaylistDeletion.TryRemove(jobId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete artifacts for job {JobId} after transfer; will rely on scheduled fallback cleanup", jobId);
        }
    }

    public void ProcessScheduledPlaylistDeletion()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _pendingPlaylistDeletion.ToArray())
        {
            if (item.Value > now) continue;

            var jobId = item.Key;
            var path = GetPlaylistDirectoryPath(jobId);

            try
            {
                if (Directory.Exists(path))
                {
                    if (DirectoryContainsLockedFiles(path))
                    {
                        _logger.LogWarning("Scheduled cleanup skipped job {JobId}: files still locked at {Path}; will retry next cycle", jobId, path);
                        continue;
                    }
                    Directory.Delete(path, true);
                    _logger.LogInformation("Scheduled fallback cleanup deleted playlist folder for job {JobId} at {Path}", jobId, path);
                }
                _pendingPlaylistDeletion.TryRemove(jobId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled cleanup failed for job {JobId} at {Path}; will retry next cycle", jobId, path);
            }
        }
    }

    public async Task<string?> ExtractThumbnailQuicklyAsync(string url, string? proxy = null)
    {
        var trimmedUrl = NormalizeInputUrl(url); // تم التعديل لتنظيف الرابط
        var cacheKey = GetPreviewCacheKey(trimmedUrl, proxy);

        if (ThumbnailCache.TryGetValue(cacheKey, out var cachedThumbnail)) return cachedThumbnail;

        try
        {
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
            startInfo.ArgumentList.Add("--flat-playlist");
            startInfo.ArgumentList.Add("--get-thumbnail");
            ApplyProxy(startInfo, proxy);
            startInfo.ArgumentList.Add(trimmedUrl);

            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";

            if (!string.IsNullOrWhiteSpace(bundledSitePackages) && Directory.Exists(bundledSitePackages))
            {
                var existingPythonPath = startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath) ? pythonPath : string.Empty;
                startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                    ? bundledSitePackages
                    : bundledSitePackages + Path.PathSeparator + existingPythonPath;
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var outputStream = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream);
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

            if (process.ExitCode != 0) return null;

            var output = Encoding.UTF8.GetString(outputStream.ToArray());
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
        var trimmedUrl = NormalizeInputUrl(url); // تم التعديل لتنظيف الرابط

        var cacheKey = GetPreviewCacheKey(trimmedUrl, proxy);

        if (PreviewCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = new PreviewData();

        try
        {
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
            ApplyProxy(startInfo, proxy);

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

            // Node.js directory on PATH for EJS challenges
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
            process.Start();

            using var outputStream = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream);
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

            var stderr = await errorTask;
            if (process.ExitCode != 0)
            {
                _logger?.LogWarning(
                    "yt-dlp preview extraction failed for {Url} (exit {Code}): {Stderr}",
                    trimmedUrl, process.ExitCode,
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

            var output = Encoding.UTF8.GetString(outputStream.ToArray());
            var jsonLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(jsonLine)) return result;

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

            double? durationSeconds = null;
            if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                durationSeconds = durEl.GetDouble();

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

                    double? tbr = null;
                    if (fmt.TryGetProperty("tbr", out var tbrEl) && tbrEl.ValueKind == JsonValueKind.Number)
                        tbr = tbrEl.GetDouble();

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
                    {
                        bestVideoByHeight[height] = (filesize, tbr);
                    }
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

            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl)) ThumbnailCache[cacheKey] = result.ThumbnailUrl;
            PreviewCache[cacheKey] = result;

            return result;
        }
        catch
        {
            return result;
        }
    }

    private async Task<PreviewData> ExtractFallbackPreviewAsync(string url, string? proxy = null)
    {
        var result = new PreviewData();
        try
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };

            if (!string.IsNullOrWhiteSpace(proxy) && Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
            {
                handler.Proxy = new System.Net.WebProxy(proxyUri);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
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
                    var rawTitle = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
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
                {
                    result.ThumbnailUrl = System.Net.WebUtility.HtmlDecode(imgMatch.Groups[1].Value);
                }

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
            _logger?.LogDebug(ex, "Fallback preview extraction failed for {Url}", url);
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

    private static string GetPreviewCacheKey(string url, string? proxy) => $"{url}\n{(string.IsNullOrWhiteSpace(proxy) ? string.Empty : proxy.Trim())}";

    private static void ApplyProxy(ProcessStartInfo startInfo, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return;
        startInfo.ArgumentList.Add("--proxy");
        startInfo.ArgumentList.Add(proxy.Trim());
    }

    private static readonly string[] _socialMediaDomains =
    {
        "instagram.com", "instagr.am",
        "facebook.com", "fb.com", "fb.watch",
        "twitter.com", "x.com",
        "tiktok.com", "vm.tiktok.com",
        "snapchat.com", "snap.com",
        "reddit.com", "pinterest.com",
    };

    private static bool IsSocialMediaUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return _socialMediaDomains.Any(d => host == d || host.EndsWith("." + d));
        }
        catch { return false; }
    }

    public void CleanupExpiredArtifacts(TimeSpan maxAge, ISet<string> protectedJobIds, bool keepDownloads = false)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        CleanupFiles(JobsPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupFiles(ArchivesPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupFiles(TempPath, cutoff, protectedJobIds, filePath => Path.GetFileNameWithoutExtension(filePath));
        CleanupDirectories(TempPath, cutoff, protectedJobIds);

        // Desktop downloads are a user's library, not temporary transfer artifacts.
        if (keepDownloads) return;

        CleanupFiles(DownloadsPath, cutoff, protectedJobIds, filePath =>
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (name.EndsWith("_title", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 6);

            var dir = Path.GetDirectoryName(filePath) ?? DownloadsPath;
            foreach (var id in protectedJobIds)
            {
                if (File.Exists(Path.Combine(dir, $"{id}_title.txt")) || name.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            return name;
        });

        CleanupDirectories(DownloadsPath, cutoff, protectedJobIds);
    }

    private void CleanupFiles(string directoryPath, DateTime cutoff, ISet<string> protectedJobIds, Func<string, string> jobIdSelector)
    {
        if (!Directory.Exists(directoryPath)) return;
        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
        {
            if (protectedJobIds.Contains(jobIdSelector(filePath))) continue;
            var info = new FileInfo(filePath);
            if (info.LastWriteTimeUtc >= cutoff || IsLocked(filePath)) continue;
            try { info.Delete(); } catch { }
        }
    }

    private void CleanupDirectories(string directoryPath, DateTime cutoff, ISet<string> protectedJobIds)
    {
        if (!Directory.Exists(directoryPath)) return;
        foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
        {
            var directoryName = Path.GetFileName(childDirectory);
            if (protectedJobIds.Contains(directoryName)) continue;
            var info = new DirectoryInfo(childDirectory);
            if (info.LastWriteTimeUtc >= cutoff || DirectoryContainsLockedFiles(childDirectory)) continue;
            try { info.Delete(recursive: true); } catch { }
        }
    }

    private bool DirectoryContainsLockedFiles(string directoryPath)
    {
        try { return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).Any(IsLocked); }
        catch { return true; }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private bool IsLocked(string filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        return _fileLocks.TryGetValue(normalizedPath, out var gate) && gate.CurrentCount == 0;
    }

    public sealed class FileLockLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _disposed;
        internal FileLockLease(string path, SemaphoreSlim gate) { Path = path; _gate = gate; }
        public string Path { get; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Release();
        }
    }
}
