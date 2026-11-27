using System;
using System.Globalization;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;

namespace NebulaServer.Services.Jobs;

internal readonly record struct ParsedProgressUpdate(
    DownloadProgress Progress,
    string ProgressPercentage,
    string SpeedText,
    string EtaText);

/// <summary>
/// Parses structured stdout/stderr protocol messages emitted by the Python media engine.
/// </summary>
internal static class EngineProtocolParser
{
    public static bool TryExtractProtocolLine(string line, string prefix, out string extractedLine)
    {
        var index = line.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            extractedLine = string.Empty;
            return false;
        }
        extractedLine = line[index..];
        return true;
    }

    public static bool TryParseProgress(string line, string jobId, out ParsedProgressUpdate update)
    {
        update = default;
        if (!TryExtractProtocolLine(line, "JOB_PROGRESS|", out var progressLine))
            return false;

        // FORMAT: JOB_PROGRESS|jobId|Status|Percent|Downloaded|Total|Speed|Eta
        var parts = progressLine.Split('|', 8);
        if (parts.Length < 8)
            return false;

        if (!double.TryParse(parts[3].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ||
            !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloaded) ||
            !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
            !double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) ||
            !long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eta))
        {
            return false;
        }

        var status = parts[2];
        var isProcessing = string.Equals(status, "PROCESSING", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "SEGMENTING", StringComparison.OrdinalIgnoreCase);

        if (eta < 0 && speed > 0 && total > downloaded)
        {
            // Downloads use bytes and B/s. FFmpeg uses microseconds and reports
            // a playback-speed multiplier (e.g. 2.4x).
            var remaining = total - downloaded;
            eta = isProcessing
                ? (long)Math.Ceiling(remaining / 1_000_000d / speed)
                : (long)Math.Ceiling(remaining / speed);
        }

        var progress = new DownloadProgress
        {
            JobId = jobId,
            Status = status,
            Percent = percent,
            DownloadedBytes = downloaded,
            TotalBytes = total,
            SpeedBytesPerSecond = speed,
            EtaSeconds = eta,
            Timestamp = DateTime.UtcNow
        };

        var speedText = speed > 0
            ? (isProcessing ? $"{speed:F2}x" : $"Internet {FormatTransferRate(speed)}")
            : string.Empty;

        update = new ParsedProgressUpdate(
            progress,
            percent > 0 ? $"{percent:F1}%" : "0%",
            speedText,
            eta >= 0 ? FormatEta(eta) : "Calculating...");

        return true;
    }

    public static bool TryParseStage(string line, out string stage, out string? details)
    {
        stage = string.Empty;
        details = null;
        if (!TryExtractProtocolLine(line, "JOB_STAGE|", out var stageLine))
            return false;

        var parts = stageLine.Split('|', 4);
        stage = parts.Length >= 3 ? parts[2] : "PROCESSING";
        details = parts.Length >= 4 ? parts[3] : null;
        return true;
    }

    public static bool TryParseResult(string line, out string outputName, out string? originalTitle)
    {
        outputName = string.Empty;
        originalTitle = null;
        if (!TryExtractProtocolLine(line, "JOB_RESULT|", out var resultLine))
            return false;

        var parts = resultLine.Split('|', 4);
        if (parts.Length < 3)
            return false;

        outputName = parts[2];
        originalTitle = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null;
        return true;
    }

    public static bool TryParseThumbnail(string line, out string thumbnailUrl)
    {
        thumbnailUrl = string.Empty;
        if (!TryExtractProtocolLine(line, "JOB_THUMBNAIL|", out var thumbnailLine))
            return false;

        var parts = thumbnailLine.Split('|', 3);
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2]))
            return false;

        thumbnailUrl = parts[2];
        return true;
    }

    public static JobState MapStageToState(string stage) =>
        stage.ToUpperInvariant() switch
        {
            "DOWNLOADING" => JobState.DOWNLOADING,
            "PROCESSING" => JobState.PROCESSING,
            "SEGMENTING" => JobState.SEGMENTING,
            "SEPARATING_VOCALS" => JobState.SEPARATING_VOCALS,
            "MERGING_AUDIO" => JobState.MERGING_AUDIO,
            "COMPLETED" => JobState.COMPLETED,
            "FAILED" => JobState.FAILED,
            _ => JobState.PROCESSING
        };

    public static string BuildDefaultStageMessage(string stage, bool isPlaylist) =>
        stage.ToUpperInvariant() switch
        {
            "DOWNLOADING" => isPlaylist ? "Downloading playlist items" : "Downloading media",
            "PROCESSING" => "Processing media",
            "SEGMENTING" => "Applying requested segment",
            "SEPARATING_VOCALS" => "Separating vocals",
            "MERGING_AUDIO" => "Merging separated audio",
            "FAILED" => "Job failed",
            _ => "Processing media"
        };

    public static string BuildFailureMessage(string stderr, string fallback)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return fallback;

        var lines = stderr.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length == 0 ? fallback : lines[^1];
    }

    public static double ParseProgressPercent(string progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
            return 0;

        var cleaned = progress.Trim().TrimEnd('%');
        return double.TryParse(cleaned, out var value) ? value : 0;
    }

    public static string FormatEta(long seconds)
    {
        if (seconds < 0) return "Calculating...";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string FormatTransferRate(double bytesPerSecond)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        return bytesPerSecond >= gb ? $"{bytesPerSecond / gb:F2} GB/s" :
               bytesPerSecond >= mb ? $"{bytesPerSecond / mb:F2} MB/s" :
               bytesPerSecond >= kb ? $"{bytesPerSecond / kb:F1} KB/s" :
               $"{bytesPerSecond:F0} B/s";
    }
}
