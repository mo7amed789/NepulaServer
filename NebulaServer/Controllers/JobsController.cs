using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;
using NebulaServer.Services;
using NebulaServer.Services.Ngrok;
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
    private readonly ILogger<JobsController> _logger;

    public JobsController(
        JobQueueManager jobs,
        StorageService storage,
        StreamingTransferService streamingTransfers,
        ServerStateService serverState,
        ILogger<JobsController> logger)
    {
        _jobs = jobs;
        _storage = storage;
        _streamingTransfers = streamingTransfers;
        _serverState = serverState;
        _logger = logger;
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
                thumbnailUrl = preview.ThumbnailUrl,
                availableQualities = preview.AvailableQualities
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview extraction failed for {Url}.", url);
            return Ok(new { thumbnailUrl = (string?)null, availableQualities = Array.Empty<string>() });
        }
    }

    [HttpPost]
    public ActionResult<JobStatusResponse> CreateJob([FromBody] JobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest("Url is required");

        var job = _jobs.CreateJob(request);

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

    // نقطة النهاية الخاصة بإلغاء المهمة
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

        if (job == null) return NotFound("Job not found.");
        if (!job.Request.IsPlaylist && job.State != JobState.COMPLETED) return BadRequest("Job not completed.");
        if (!job.Request.IsPlaylist && string.IsNullOrWhiteSpace(job.OutputFileName))
            return NotFound("Output file name is missing from job metadata.");

        try
        {
            string fileToServe;
            string mimeType;
            string downloadFileName;

            if (job.Request.IsPlaylist)
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
                var outputFileName = job.OutputFileName ?? string.Empty;

                fileToServe = _storage.GetDownloadFilePath(outputFileName);
                mimeType = Path.GetExtension(outputFileName).ToLower() switch
                {
                    ".mp4" => "video/mp4",
                    ".mp3" => "audio/mpeg",
                    ".wav" => "audio/wav",
                    _ => "application/octet-stream"
                };
                downloadFileName = outputFileName;

                if (!System.IO.File.Exists(fileToServe))
                    return NotFound($"File not found on server disk. Expected: {fileToServe}");
            }

            _jobs.RemoveJob(job.JobId);

            HttpContext.Response.OnCompleted(() =>
            {
                try
                {
                    if (System.IO.File.Exists(fileToServe))
                    {
                        System.IO.File.Delete(fileToServe);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete downloaded file/archive: {File}", fileToServe);
                }

                return Task.CompletedTask;
            });

            return PhysicalFile(fileToServe, mimeType, downloadFileName);
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
        var fileToServe = Path.Combine(_storage.GetPlaylistDirectoryPath(job.JobId), item.FileName);
        if (!System.IO.File.Exists(fileToServe))
            return NotFound($"File not found on server disk. Expected: {fileToServe}");

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
            return NotFound(ex.Message);
        }

        Response.OnCompleted(() =>
        {
            if (HttpContext.RequestAborted.IsCancellationRequested)
            {
                session.Release(item);
                _jobs.TrySetPlaylistItemState(job, item.Index, TransferState.Ready);
            }
            else
            {
                session.MarkCompleted(item);
                item.State = TransferState.Completed;
                _jobs.TrySetPlaylistItemState(job, item.Index, TransferState.Completed);

                // إذا انتهى تنزيل جميع الملفات
                if (job.CompletedItems.Values.All(x => x.State == TransferState.Completed))
                {
                    _storage.SchedulePlaylistDeletion(
                    job.JobId,
                    TimeSpan.FromMinutes(5));
                    _jobs.RemoveJob(job.JobId);
                }
            }

            _streamingTransfers.CompleteTransfer(transfer);
            _logger.LogInformation("Stream transfer finished for {JobId}:{Index}", job.JobId, item.Index);
            return transfer.DisposeAsync().AsTask();
        });

        HttpContext.RequestAborted.Register(() =>
        {
            session.Release(item);
        });

        return File(
            transfer.Stream,
            transfer.ContentType,
            Path.GetFileName(transfer.FilePath),
            enableRangeProcessing: true);
    }
}
