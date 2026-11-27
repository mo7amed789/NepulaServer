using System.Text.Json;
using NebulaServer.Helpers;
using NebulaServer.Models.Jobs;

namespace NebulaServer.Services.Jobs;

/// <summary>
/// Blocks starting a download that this server already has, until the caller
/// explicitly asks to repeat it (<see cref="NebulaServer.Models.JobRequest.AllowDuplicate"/>).
/// Matching is on the normalized URL plus the requested format and quality, so
/// grabbing the MP3 of a video that was saved as 720p MP4 is not a duplicate.
/// </summary>
public sealed class DuplicateDownloadGuard
{
    // Anything that is not one of these is treated as still in flight, so a new
    // state added later cannot slip a second download past the guard.
    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMPLETED", "FAILED", "CANCELED", "CANCELLED"
    };

    private readonly JobStore _store;
    private readonly ILogger<DuplicateDownloadGuard> _logger;

    public DuplicateDownloadGuard(JobStore store, ILogger<DuplicateDownloadGuard> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Returns the job that already covers this request, or null when the
    /// download is new (or its earlier file is gone).
    /// </summary>
    public ExistingDownload? Find(NebulaServer.Models.JobRequest request)
    {
        var signature = Signature(request);

        // 1. Something for the same media is queued or running right now.
        foreach (var snapshot in _store.GetUnfinished())
        {
            if (TerminalStates.Contains(snapshot.Status))
                continue;

            if (Matches(snapshot, signature))
                return ExistingDownload.From(snapshot, isActive: true);
        }

        // 2. It was already downloaded and the file is still on disk.
        foreach (var snapshot in _store.GetHistoryWithoutTransfers())
        {
            if (!string.Equals(snapshot.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                continue;

            // A deleted/archived file means the user may download it again freely.
            if (!OutputFileExists(snapshot.OutputPath))
                continue;

            if (Matches(snapshot, signature))
                return ExistingDownload.From(snapshot, isActive: false);
        }

        return null;
    }

    private bool Matches(JobSnapshot snapshot, string signature)
    {
        var existing = SignatureOf(snapshot);
        if (existing is null)
            return false;

        // Fall back to the URL alone when the stored request cannot be read, but
        // only for an exact URL match on the same request shape.
        return string.Equals(existing, signature, StringComparison.OrdinalIgnoreCase);
    }

    private static string Signature(NebulaServer.Models.JobRequest request) =>
        BuildSignature(request.Url, request.Format, request.Quality, request.IsPlaylist);

    private static string? SignatureOf(JobSnapshot snapshot)
    {
        NebulaServer.Models.JobRequest? stored = null;
        if (!string.IsNullOrWhiteSpace(snapshot.RequestJson))
        {
            try
            {
                stored = JsonSerializer.Deserialize<NebulaServer.Models.JobRequest>(snapshot.RequestJson);
            }
            catch (JsonException)
            {
                stored = null;
            }
        }

        if (stored is null)
            return string.IsNullOrWhiteSpace(snapshot.Url) ? null : BuildSignature(snapshot.Url, null, null, false);

        return BuildSignature(
            string.IsNullOrWhiteSpace(stored.Url) ? snapshot.Url : stored.Url,
            stored.Format,
            stored.Quality,
            stored.IsPlaylist);
    }

    /// <summary>
    /// JobStore keeps the output as a bare file name ("clip.mp4"), relative to
    /// the downloads folder, so a plain File.Exists would always be false.
    /// </summary>
    private static bool OutputFileExists(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return false;

        try
        {
            var resolved = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(RuntimePaths.Downloads, outputPath);

            return File.Exists(resolved);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string BuildSignature(string url, string? format, string? quality, bool isPlaylist)
    {
        static string Clean(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
        return $"{Clean(url)}|{Clean(format)}|{Clean(quality)}|{isPlaylist}";
    }
}

/// <summary>An existing job that already covers a download request.</summary>
public sealed record ExistingDownload(
    string JobId,
    string Title,
    string FileName,
    DateTime CreatedAt,
    bool IsActive,
    double Progress)
{
    public static ExistingDownload From(JobSnapshot snapshot, bool isActive) => new(
        snapshot.Id,
        string.IsNullOrWhiteSpace(snapshot.Title) ? "This link" : snapshot.Title,
        string.IsNullOrWhiteSpace(snapshot.OutputPath) ? string.Empty : Path.GetFileName(snapshot.OutputPath),
        snapshot.CreatedAt,
        isActive,
        snapshot.Progress);
}
