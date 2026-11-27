using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Media;
using NebulaServer.Services.MediaExtraction;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/media")]
[Route("api/v1/media")]
public class MediaController : ControllerBase
{
    private readonly MediaExtractionEngine _extractionEngine;
    private readonly ILogger<MediaController> _logger;
    private readonly IHostEnvironment _environment;

    public MediaController(
        MediaExtractionEngine extractionEngine,
        ILogger<MediaController> logger,
        IHostEnvironment environment)
    {
        _extractionEngine = extractionEngine;
        _logger = logger;
        _environment = environment;
    }

    [HttpGet("extract")]
    public async Task<IActionResult> Extract([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "Url is required." });
        }

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var invalid = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(
                    400, HttpContext, $"'{url}' is not an absolute URL.",
                    "Include the scheme, for example https://www.youtube.com/watch?v=... and URL-encode the query parameter.");
                return StatusCode(invalid.Error.Status, invalid);
            }

            var result = await _extractionEngine.ExtractAsync(uri, ct);
            
            return Ok(new
            {
                success = true,
                data = result
            });
        }
        catch (MediaExtractionException ex)
        {
            _logger.LogWarning(ex, "Media extraction failed for {Url} with code {Code}", url, ex.Code);

            var payload = NebulaServer.Services.Errors.ErrorCatalog
                .FromException(ex, HttpContext,
                    NebulaServer.Services.Errors.ErrorCatalog.ShouldIncludeDetails(_environment))
                .WithLegacyCode(ex.Code.ToString());

            return StatusCode(payload.Error.Status, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during media extraction for {Url}", url);
            
            return StatusCode(500, new
            {
                success = false,
                error = new
                {
                    code = ExtractionErrorCode.ExtractionFailed.ToString(),
                    message = "An unexpected error occurred while extracting media."
                }
            });
        }
    }
}
