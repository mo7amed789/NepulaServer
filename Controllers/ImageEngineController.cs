using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.ImageEngine;
using NebulaServer.Services.ImageEngine;

namespace NebulaServer.Controllers
{
    [ApiController]
    [Route("api/images")]
    public class ImageEngineController : ControllerBase
    {
        private readonly ImageExtractionEngine _extractionEngine;
        private readonly ImageDownloadManager _downloadManager;

        public ImageEngineController(ImageExtractionEngine extractionEngine, ImageDownloadManager downloadManager)
        {
            _extractionEngine = extractionEngine;
            _downloadManager = downloadManager;
        }

        public class AnalyzeRequest
        {
            [Required]
            public string Url { get; set; } = null!;
        }

        [HttpPost("analyze")]
        public async Task<IActionResult> AnalyzeAsync([FromBody] AnalyzeRequest request, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                return BadRequest(new { error = "Invalid URL format." });
            }

            try
            {
                await NebulaServer.Helpers.SafeOutboundHttp.EnsureSafeUriAsync(uri, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return BadRequest(new { error = ex.Message });
            }

            try
            {
                var result = await _extractionEngine.ExtractAsync(uri, cancellationToken);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { error = "An unexpected error occurred during analysis." });
            }
        }

        public class DownloadRequest
        {
            [Required]
            public string Url { get; set; } = null!;

            public string? SourceUrl { get; set; }
        }

        [HttpPost("download")]
        public async Task<IActionResult> DownloadAsync([FromBody] DownloadRequest request, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                return BadRequest(new { error = "Invalid URL format." });
            }

            try
            {
                await NebulaServer.Helpers.SafeOutboundHttp.EnsureSafeUriAsync(uri, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return BadRequest(new { error = ex.Message });
            }

            // We need a dummy candidate just to hold the URL for the downloader
            var candidate = new ImageCandidate { Url = request.Url, Source = "Direct Request" };
            
            var downloadsDir = Path.Combine(AppContext.BaseDirectory, "Downloads", "Images");
            
            try
            {
                Uri? sourceUri = null;
                if (!string.IsNullOrWhiteSpace(request.SourceUrl))
                    Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out sourceUri);

                var filePath = await _downloadManager.DownloadAndValidateAsync(
                    candidate, downloadsDir, sourceUri, cancellationToken);
                
                // Return the file stream
                var memory = new MemoryStream(await System.IO.File.ReadAllBytesAsync(filePath, cancellationToken));
                System.IO.File.Delete(filePath); // Clean up immediately after reading into memory
                
                memory.Position = 0;
                var fileName = Path.GetFileName(filePath);
                
                return File(memory, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to download or validate image.", details = ex.Message });
            }
        }
    }
}
