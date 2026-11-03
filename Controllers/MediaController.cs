using System;
using System.Threading.Tasks;
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

    public MediaController(MediaExtractionEngine extractionEngine, ILogger<MediaController> logger)
    {
        _extractionEngine = extractionEngine;
        _logger = logger;
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
                return BadRequest(new { error = "Invalid URL format." });
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
            
            return BadRequest(new
            {
                success = false,
                error = new
                {
                    code = ex.Code.ToString(),
                    message = ex.Message
                }
            });
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
