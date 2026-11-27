using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.ImageEngine;
using NebulaServer.Services.Core;
using NebulaServer.Services.Errors;
using NebulaServer.Services.ImageEngine;

namespace NebulaServer.Controllers
{
    [ApiController]
    [Route("api/images")]
    public class ImageEngineController : ControllerBase
    {
        private readonly ImageExtractionEngine _extractionEngine;
        private readonly ImageDownloadManager _downloadManager;
        private readonly LicenseService _license;

        public ImageEngineController(
            ImageExtractionEngine extractionEngine,
            ImageDownloadManager downloadManager,
            LicenseService license)
        {
            _extractionEngine = extractionEngine;
            _downloadManager = downloadManager;
            _license = license;
        }

        private ObjectResult? RejectIfUnlicensed()
        {
            if (_license.IsActivated)
                return null;

            var payload = ErrorCatalog.LicenseRequired(HttpContext, _license.IsLicensingServerConfigured);
            return StatusCode(payload.Error.Status, payload);
        }

        public class AnalyzeRequest
        {
            [Required]
            public string Url { get; set; } = null!;
        }

        [HttpPost("analyze")]
        public async Task<IActionResult> AnalyzeAsync([FromBody] AnalyzeRequest request, CancellationToken cancellationToken)
        {
            if (RejectIfUnlicensed() is { } licenseError)
                return licenseError;

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                var invalidUrl = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, HttpContext,
                    "The Url field is not an absolute URI.",
                    "Send an absolute http:// or https:// address in the Url field, for example https://example.com/gallery.");
                return StatusCode(invalidUrl.Error.Status, invalidUrl);
            }

            try
            {
                await NebulaServer.Helpers.SafeOutboundHttp.EnsureSafeUriAsync(uri, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                var blocked = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, HttpContext, ex.Message,
                    "Use a public page. Loopback, private-network and metadata addresses are blocked on purpose, and the site must answer over http(s).");
                return StatusCode(blocked.Error.Status, blocked);
            }

            try
            {
                var result = await _extractionEngine.ExtractAsync(uri, cancellationToken);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                var analysis = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, HttpContext, ex.Message,
                    "Check the page loads in a browser without logging in, and that it contains images. Script-driven galleries may need the page to be reached over its canonical URL.");
                return StatusCode(analysis.Error.Status, analysis);
            }
            catch (Exception ex)
            {
                var unexpected = NebulaServer.Services.Errors.ErrorCatalog.FromException(ex, HttpContext, false);
                return StatusCode(unexpected.Error.Status, unexpected);
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
            if (RejectIfUnlicensed() is { } licenseError)
                return licenseError;

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                var invalidUrl = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, HttpContext,
                    "The Url field is not an absolute URI.",
                    "Send an absolute http:// or https:// address in the Url field, for example https://example.com/gallery.");
                return StatusCode(invalidUrl.Error.Status, invalidUrl);
            }

            try
            {
                await NebulaServer.Helpers.SafeOutboundHttp.EnsureSafeUriAsync(uri, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                var blocked = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(400, HttpContext, ex.Message,
                    "Use a public page. Loopback, private-network and metadata addresses are blocked on purpose, and the site must answer over http(s).");
                return StatusCode(blocked.Error.Status, blocked);
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
                var failed = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(502, HttpContext,
                    $"The image could not be downloaded or validated: {ex.Message}",
                    "The host may block direct image requests or serve a response that is not an image. Open the image URL in a browser; if it needs a referer or login, download it from the page instead.");
                return StatusCode(failed.Error.Status, failed);
            }
        }
    }
}
