using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;
using NebulaServer.Services;
using NebulaServer.Services.Jobs;
using NebulaServer.Services.Server;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/jobs")]
[Route("api/v1/jobs")]
public class JobsController : ControllerBase
{
    private readonly JobQueueManager _jobs;
    private readonly StorageService _storage;
    private readonly StreamingTransferService _streamingTransfers;
    private readonly ServerStateService _serverState;
    private readonly JobStore _store;
    private readonly ILogger<JobsController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public JobsController(
        JobQueueManager jobs,
        StorageService storage,
        StreamingTransferService streamingTransfers,
        ServerStateService serverState,
        JobStore store,
        ILogger<JobsController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _jobs = jobs;
        _storage = storage;
        _streamingTransfers = streamingTransfers;
        _serverState = serverState;
        _store = store;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        return Ok(new
        {
            success = true,
            server = _serverState.Get()
        });
    }

    [HttpGet("{id}/progress")]
    public IActionResult GetJobProgress(string id)
    {
        var job = _jobs.GetJob(id);
        if (job?.CurrentProgress != null)
        {
            return Ok(job.CurrentProgress);
        }

        var historyJob = _store.GetAllHistory().FirstOrDefault(j => j.Id == id);
        if (historyJob != null)
        {
            return Ok(new NebulaServer.Models.Jobs.DownloadProgress
            {
                JobId = historyJob.Id,
                Status = historyJob.Status,
                Percent = historyJob.Progress,
                DownloadedBytes = 0,
                TotalBytes = 0,
                SpeedBytesPerSecond = -1,
                EtaSeconds = -1,
                Timestamp = historyJob.CreatedAt
            });
        }

        if (job != null)
        {
            return Ok(new NebulaServer.Models.Jobs.DownloadProgress
            {
                JobId = job.JobId,
                Status = job.State.ToString(),
                Percent = 0,
                DownloadedBytes = 0,
                TotalBytes = 0,
                SpeedBytesPerSecond = -1,
                EtaSeconds = -1,
                Timestamp = job.CreatedAt
            });
        }

        return NotFound(new { error = "Job not found" });
    }

    [HttpGet("history")]
    public IActionResult GetHistory()
    {
        var history = _store.GetAllHistory();
        return Ok(history);
    }

    [HttpDelete("history/{id}")]
    public IActionResult DeleteHistory(string id)
    {
        var historyJob = _store.GetAllHistory().FirstOrDefault(j => j.Id == id);
        if (historyJob != null && !string.IsNullOrWhiteSpace(historyJob.OutputPath))
        {
            try
            {
                var fileName = Path.GetFileName(historyJob.OutputPath);
                var filePath = _storage.GetDownloadFilePath(fileName);
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
                else if (System.IO.File.Exists(historyJob.OutputPath))
                {
                    System.IO.File.Delete(historyJob.OutputPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete physical file for history job {JobId}", id);
            }
        }
        _jobs.RemoveJob(id);
        _store.DeleteJob(id);
        return Ok();
    }

    [HttpDelete("history")]
    public IActionResult ClearHistory()
    {
        var history = _store.GetAllHistory();
        foreach (var historyJob in history)
        {
            if (!string.IsNullOrWhiteSpace(historyJob.OutputPath))
            {
                try
                {
                    var fileName = Path.GetFileName(historyJob.OutputPath);
                    var filePath = _storage.GetDownloadFilePath(fileName);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                    else if (System.IO.File.Exists(historyJob.OutputPath))
                    {
                        System.IO.File.Delete(historyJob.OutputPath);
                    }
                }
                catch { }
            }
            _jobs.RemoveJob(historyJob.Id);
        }
        _store.ClearHistory();
        return Ok();
    }

    [HttpGet("thumbnail-proxy")]
    public async Task<IActionResult> DownloadThumbnailProxy([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return BadRequest("Invalid URL scheme.");

        try
        {
            await SafeOutboundHttp.EnsureSafeUriAsync(uri, HttpContext.RequestAborted);
            var client = _httpClientFactory.CreateClient("ImageDownloaderClient");
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
            var response = await client.SendAsync(
                request,
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                HttpContext.RequestAborted);

            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("image/"))
                return BadRequest("Not an image.");
                
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > 10 * 1024 * 1024)
                return BadRequest("Image too large.");

            var stream = await response.Content.ReadAsStreamAsync();
            return File(stream, contentType, "thumbnail.jpg");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Thumbnail proxy rejected or failed to retrieve {Url}.", url);
            return BadRequest("Unable to retrieve this thumbnail safely.");
        }
    }

    [HttpGet("preview")]
    public async Task<IActionResult> GetPreview([FromQuery] string url, [FromQuery] string? proxy = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("Url is required");

        try
        {
            var preview = await _storage.ExtractPreviewDataAsync(url, proxy);
            return Ok(new
            {
                title = preview.Title,
                thumbnailUrl = preview.ThumbnailUrl,
                availableQualities = preview.AvailableQualities
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview extraction failed for {Url}.", url);
            return Ok(new { title = (string?)null, thumbnailUrl = (string?)null, availableQualities = Array.Empty<string>() });
        }
    }

    [HttpPost]
    public ActionResult<JobStatusResponse> CreateJob([FromBody] JobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest("Url is required");

        request.Url = StorageService.NormalizeInputUrl(request.Url);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri) ||
            (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("A valid HTTP or HTTPS URL is required.");
        }

        var job = _jobs.CreateJob(request);

        // Keep the complete job configuration visible in the server console so
        // download issues can be diagnosed without attaching a debugger.
        _logger.LogInformation(
            "Job details {JobId}: Url={Url}, Quality={Quality}, Format={Format}, " +
            "Playlist={IsPlaylist}, PlaylistRange={PlaylistStart}-{PlaylistEnd}, " +
            "ExtractVocals={ExtractVocals}, Segment={SegmentStart}-{SegmentEnd}, " +
            "DataSaverMode={DataSaverMode}, MaxSpeedBytes={MaxSpeedBytes}, ProxyConfigured={ProxyConfigured}",
            job.JobId,
            request.Url,
            request.Quality,
            request.Format,
            request.IsPlaylist,
            request.PlaylistStart,
            request.PlaylistEnd,
            request.ExtractVocals,
            request.SegmentStart,
            request.SegmentEnd,
            request.DataSaverMode,
            request.MaxSpeedBytes,
            !string.IsNullOrWhiteSpace(request.Proxy));

        return Ok(new JobStatusResponse
        {
            JobId = job.JobId,
            State = job.State.ToString(),
            ProgressPercentage = "0%",
            Speed = "",
            Eta = "",
            Message = job.Message,
            CompletedItems = job.CompletedItems.Values.OrderBy(item => item.Index).ToList(),
            ManifestVersion = job.ManifestVersion,
            EventSequence = job.EventSequence,
            CompletedCount = job.CompletedItems.Values.Count(item => item.State == TransferState.Completed),
            TotalCount = job.CompletedItems.Count,
            ThumbnailUrl = string.Empty
        });
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelJob(string id)
    {
        var canceled = await _jobs.CancelJobAsync(id);

        if (canceled)
        {
            return Ok(new { message = "Job successfully canceled." });
        }

        return BadRequest(new { message = "Job not found or already finished." });
    }

    [HttpGet("{id}")]
    public ActionResult<JobStatusResponse> GetStatus(string id)
    {
        var job = _jobs.GetJob(id);

        if (job == null)
            return NotFound();

        return Ok(new JobStatusResponse
        {
            JobId = job.JobId,
            State = job.State.ToString(),
            ProgressPercentage = job.ProgressPercentage,
            Speed = job.Speed,
            Eta = job.Eta,
            Message = job.Message,
            OutputFileName = job.OutputFileName,
            CompletedItems = job.CompletedItems.Values.OrderBy(item => item.Index).ToList(),
            ManifestVersion = job.ManifestVersion,
            EventSequence = job.EventSequence,
            CompletedCount = job.CompletedItems.Values.Count(item => item.State == TransferState.Completed),
            TotalCount = job.CompletedItems.Count,
            ThumbnailUrl = job.ThumbnailUrl
        });
    }

    [HttpGet("{id}/manifest")]
    public ActionResult<StreamingManifest> GetStreamingManifest(string id)
    {
        var job = _jobs.GetJob(id);

        if (job == null)
            return NotFound();

        if (!job.Request.IsPlaylist)
        {
            return Ok(new StreamingManifest
            {
                JobId = job.JobId,
                Playlist = false,
                GeneratedAtUtc = DateTime.UtcNow,
                Items = Array.Empty<StreamingManifestItem>()
            });
        }

        return Ok(_streamingTransfers.BuildManifest(job));
    }

    [HttpGet("{id}/events")]
    public ActionResult<TransferEventsResponse> GetTransferEvents(string id, [FromQuery] long after = 0)
    {
        var job = _jobs.GetJob(id);

        if (job == null)
            return NotFound();

        var events = _jobs.GetTransferEventsAfter(id, after);

        return Ok(new TransferEventsResponse
        {
            JobId = job.JobId,
            After = after,
            Latest = job.EventSequence,
            Events = events
        });
    }

    [HttpGet("{id}/download")]
    public IActionResult DownloadFile(string id, [FromQuery] bool archive = false)
    {
        _logger.LogWarning("=== DOWNLOAD ENDPOINT HIT === JobId={JobId}", id);
        var job = _jobs.GetJob(id);
        JobSnapshot? historyJob = null;

        if (job == null)
        {
            historyJob = _store.GetAllHistory().FirstOrDefault(j => j.Id == id);
            if (historyJob == null) return NotFound("Job not found.");
        }

        if (job != null)
        {
            if (!job.Request.IsPlaylist && job.State != JobState.COMPLETED) return BadRequest("Job not completed.");
            if (!job.Request.IsPlaylist && string.IsNullOrWhiteSpace(job.OutputFileName))
                return NotFound("Output file name is missing from job metadata.");
        }

        try
        {
            string fileToServe;
            string mimeType;
            string downloadFileName;

            if (job != null && job.Request.IsPlaylist)
            {
                var playlistFiles = _storage.GetPlaylistFiles(job.JobId);

                if (!archive)
                {
                    return Ok(new PlaylistDownloadResponse
                    {
                        JobId = job.JobId,
                        Playlist = true,
                        TotalFiles = playlistFiles.Count,
                        NextDownloadUrl = Url.Action(
                            nameof(DownloadNextPlaylistItem),
                            "Jobs",
                            new { id = job.JobId }),
                        Files = playlistFiles.Select((file, index) =>
                        {
                            var itemIndex = index + 1;

                            return new PlaylistDownloadItem
                            {
                                Index = itemIndex,
                                FileName = Path.GetFileName(file),
                                DownloadUrl = Url.Action(
                                    nameof(DownloadPlaylistItem),
                                    "Jobs",
                                    new { id = job.JobId, index = itemIndex }) ?? string.Empty,
                                NextDownloadUrl = itemIndex < playlistFiles.Count
                                    ? Url.Action(
                                        nameof(DownloadPlaylistItem),
                                        "Jobs",
                                        new { id = job.JobId, index = itemIndex + 1 })
                                    : null
                            };
                        }).ToArray()
                    });
                }

                if (job.State != JobState.COMPLETED)
                    return BadRequest("Playlist archive is only available after the job completes.");

                fileToServe = _storage.CreatePlaylistArchive(job.JobId, job.OutputFileName);
                mimeType = "application/zip";
                downloadFileName = Path.GetFileName(fileToServe);
            }
            else
            {
                var outputFileName = (job != null ? job.OutputFileName : Path.GetFileName(historyJob?.OutputPath)) ?? string.Empty;

                fileToServe = _storage.GetDownloadFilePath(outputFileName);
                if (!System.IO.File.Exists(fileToServe) && historyJob?.OutputPath != null && System.IO.File.Exists(historyJob.OutputPath))
                {
                    fileToServe = historyJob.OutputPath;
                }

                mimeType = Path.GetExtension(fileToServe).ToLower() switch
                {
                    ".mp4" => "video/mp4",
                    ".webm" => "video/webm",
                    ".mkv" => "video/x-matroska",
                    ".mp3" => "audio/mpeg",
                    ".m4a" => "audio/mp4",
                    ".wav" => "audio/wav",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".zip" => "application/zip",
                    _ => "application/octet-stream"
                };
                downloadFileName = Path.GetFileName(fileToServe);

                if (!System.IO.File.Exists(fileToServe))
                    return NotFound($"File not found on server disk. Expected: {fileToServe}");
            }

            var safeAsciiName = "video_" + id[..Math.Min(8, id.Length)] + Path.GetExtension(downloadFileName);
            var encodedFileName = Uri.EscapeDataString(downloadFileName);

            Response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeAsciiName}\"; filename*=UTF-8''{encodedFileName}";

            return PhysicalFile(fileToServe, mimeType);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("{id}/download/{index:int}")]
    public async Task<IActionResult> DownloadPlaylistItem(string id, int index)
    {
        var job = _jobs.GetJob(id);

        if (job == null) return NotFound("Job not found.");
        if (!job.Request.IsPlaylist) return BadRequest("Job is not a playlist.");

        if (!job.CompletedItems.TryGetValue(index, out var item))
            return NotFound("Playlist item not ready yet.");

        var session = _streamingTransfers.GetOrCreateSession(job.JobId);
        var nextReady = job.CompletedItems.Values
            .Where(i => !session.IsDelivered(i.Index))
            .OrderBy(i => i.Index)
            .FirstOrDefault();

        if (nextReady == null || nextReady.Index != index)
        {
            return Conflict(new
            {
                message = "Download playlist items in order using /download/next."
            });
        }

        if (!session.TryReserve(item))
            return Conflict(new { message = "Playlist item already downloaded or in progress." });

        _jobs.TrySetPlaylistItemState(job, item.Index, TransferState.Transferring);

        return await ServePlaylistItemAsync(job, item, session);
    }

    [HttpGet("{id}/download/next")]
    public async Task<IActionResult> DownloadNextPlaylistItem(string id)
    {
        var job = _jobs.GetJob(id);

        if (job == null) return NotFound("Job not found.");
        if (!job.Request.IsPlaylist) return BadRequest("Job is not a playlist.");

        var session = _streamingTransfers.GetOrCreateSession(job.JobId);
        var item = job.CompletedItems.Values
            .Where(i => !session.IsDelivered(i.Index))
            .OrderBy(i => i.Index)
            .FirstOrDefault();

        if (item == null)
        {
            return Accepted(new
            {
                ready = false,
                message = "No playlist item is ready yet."
            });
        }

        if (!session.TryReserve(item))
            return Conflict(new { message = "Playlist item already downloaded or in progress." });

        _jobs.TrySetPlaylistItemState(job, item.Index, TransferState.Transferring);

        return await ServePlaylistItemAsync(job, item, session);
    }

    private async Task<IActionResult> ServePlaylistItemAsync(DownloadJob job, PlaylistItemResult item, DownloadSession session)
    {
        void ReleaseReservation()
        {
            session.Release(item);
            _jobs.TrySetPlaylistItemState(job, item.Index, TransferState.Ready);
        }

        var fileToServe = Path.Combine(_storage.GetPlaylistDirectoryPath(job.JobId), item.FileName);
        if (!System.IO.File.Exists(fileToServe))
        {
            ReleaseReservation();
            return NotFound($"File not found on server disk. Expected: {fileToServe}");
        }

        FileTransferSession transfer;
        try
        {
            transfer = await _streamingTransfers.OpenPlaylistItemAsync(
                job,
                item,
                session.SessionId,
                HttpContext.RequestAborted);
        }
        catch (FileNotFoundException ex)
        {
            ReleaseReservation();
            return NotFound(ex.Message);
        }
        catch
        {
            ReleaseReservation();
            throw;
        }

        Response.OnCompleted(() =>
        {
            var fullRepresentationCompleted =
                !HttpContext.RequestAborted.IsCancellationRequested &&
                HttpMethods.IsGet(HttpContext.Request.Method) &&
                HttpContext.Response.StatusCode == StatusCodes.Status200OK &&
                !HttpContext.Request.Headers.ContainsKey("Range") &&
                transfer.WasFullyRead;

            if (!fullRepresentationCompleted)
            {
                ReleaseReservation();
            }
            else
            {
                session.MarkCompleted(item);
                item.State = TransferState.Completed;
                _jobs.TrySetPlaylistItemState(job, item.Index, TransferState.Completed);

                if (job.CompletedItems.Values.All(x => x.State == TransferState.Completed))
                {
                    // ??? ????? ????? ?? ??? ?? ???? ???? ??????? ??? ???? ??????? ?? ?????? 
                    // ???? ???? ?????? ????? ????????? ????? ???? ???? ?? ??????? ??? Streaming
                    _storage.SchedulePlaylistDeletion(
                    job.JobId,
                    TimeSpan.FromSeconds(10));
                    _jobs.RemoveJob(job.JobId);
                }
            }

            _streamingTransfers.CompleteTransfer(transfer);
            _logger.LogInformation("Stream transfer finished for {JobId}:{Index}", job.JobId, item.Index);
            return transfer.DisposeAsync().AsTask();
        });

        return File(
            transfer.Stream,
            transfer.ContentType,
            Path.GetFileName(transfer.FilePath),
            enableRangeProcessing: true);
    }
}
