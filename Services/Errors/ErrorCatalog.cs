using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NebulaServer.Models.Errors;
using NebulaServer.Models.Media;

namespace NebulaServer.Services.Errors;

/// <summary>
/// Turns exceptions and bare status codes into an <see cref="ApiErrorResponse"/>
/// that names the cause and the fix. Every branch here exists because that
/// failure was observed in the field; unknown exceptions fall through to a
/// generic entry that points at the log with the request id.
/// </summary>
public static class ErrorCatalog
{
    public const string CookieFileHint = @"C:\ProgramData\Tanzeel\cookies.txt";

    // =========================================================================
    // Exceptions
    // =========================================================================

    public static ApiErrorResponse FromException(
        Exception exception, HttpContext context, bool includeDetails)
    {
        var requestId = RequestId(context);
        var (status, code, title, reason, fix, retryable) = Describe(exception, context);
        return Build(status, code, title, reason, fix, retryable, requestId, exception, includeDetails);
    }

    private static (int Status, string Code, string Title, string Reason, string Fix, bool Retryable)
        Describe(Exception exception, HttpContext context)
    {
        switch (exception)
        {
            case MediaExtractionException media:
                return DescribeExtraction(media);

            case BadHttpRequestException bad:
                return ((int)bad.StatusCode, "bad_request", "The request could not be read",
                    SafeMessage(bad),
                    "Check the URL, query string and body against the API table in README.md.",
                    false);

            case FileNotFoundException file:
                {
                    var path = file.Message.Contains("Python", StringComparison.OrdinalIgnoreCase)
                        || file.Message.Contains("python", StringComparison.Ordinal);
                    return path
                        ? (503, "python_runtime_missing", "Media engine is not installed",
                            "The server could not start the Python media engine: no usable python.exe or nebula_engine.py was found.",
                            @"Install Python 3.11+ and add it to PATH, or place a runtime at C:\Users\RDP\NepulaServer\Python\python.exe; install the engine dependencies with: python -m pip install yt-dlp requests curl_cffi",
                            false)
                        : (500, "file_missing", "A required file is missing",
                            $"The server looked for a file it needs but it does not exist: {SafeMessage(file)}",
                            @"Restore the missing file (FFmpeg builds unpack next to NebulaServer.exe) or reinstall the server.",
                            true);
                }

            case DirectoryNotFoundException dir:
                return (500, "directory_missing", "A required folder is missing",
                    $"The server could not reach a folder it needs: {SafeMessage(dir)}",
                    @"Create the folder, or check that TANZEEL_DATA_DIR and the install path are valid and writable.",
                    true);

            case UnauthorizedAccessException:
                return (500, "access_denied", "The server is not allowed to touch that file",
                    "Windows refused a read or write. This is usually a file still open in another program, or a folder the service account cannot write.",
                    @"Close the program holding the file (media player, Explorer preview) and retry; otherwise run the server as an account that can write to C:\ProgramData\Tanzeel.",
                    false);

            case IOException io:
                return (500, "io_error", "Disk operation failed",
                    $"A read or write failed: {SafeMessage(io)}",
                    @"Check free space on the drive holding C:\ProgramData\Tanzeel, make sure no other program holds the same file, then retry.",
                    true);

            case HttpRequestException http:
                return (502, "upstream_unreachable", "The server could not reach the remote service",
                    $"An outbound HTTP request failed: {SafeMessage(http)}",
                    @"Check the server's internet connection or proxy. For the licensing link, confirm Licensing:BaseUrl points at a reachable NebulaLicensingServer.",
                    true);

            case TaskCanceledException or TimeoutException or OperationCanceledException
                when !context.RequestAborted.IsCancellationRequested:
                return (504, "upstream_timeout", "The remote service took too long",
                    "The upstream platform or the licensing server did not answer in time.",
                    @"Retry the request; if it keeps timing out choose a lower quality or raise Licensing:RequestTimeoutSeconds.",
                    true);

            case JsonException:
                return (400, "invalid_json", "The request body is not valid JSON",
                    $"The JSON sent to this endpoint could not be parsed: {SafeMessage(exception)}",
                    @"Send a JSON body with Content-Type: application/json and check quoting and trailing commas.",
                    false);

            case ArgumentException argument:
                return (400, "invalid_argument", "A request value is not acceptable",
                    SafeMessage(argument),
                    "Correct the value and retry. Formats, qualities and time segments must match the documented options.",
                    false);

            case InvalidOperationException invalid:
                return (409, "invalid_state", "The server is in a state that conflicts with this request",
                    SafeMessage(invalid),
                    "Wait for the running job to finish (see the Downloads list), then retry.",
                    true);

            case NotImplementedException or NotSupportedException:
                return (501, "not_implemented", "This feature is not available yet",
                    "The endpoint exists but the requested capability is not implemented in this build.",
                    "Use a supported platform or update the server to a build that includes it.",
                    false);

            case DllNotFoundException or TypeInitializationException:
                return (500, "native_dependency_missing", "A native component failed to load",
                    SafeMessage(exception),
                    @"Install the missing runtime component (for example the Visual C++ Redistributable) and restart the server.",
                    false);

            default:
                return (500, "unexpected_error", "Unexpected server error",
                    "The server hit an unhandled condition while processing the request.",
                    $@"Retry once. If it repeats, open the newest log in C:\ProgramData\Tanzeel\Logs and search for the request id shown with this response; report it with that id.",
                    true);
        }
    }

    private static (int Status, string Code, string Title, string Reason, string Fix, bool Retryable)
        DescribeExtraction(MediaExtractionException exception) => exception.Code switch
        {
            ExtractionErrorCode.UnsupportedUrl =>
                (400, "unsupported_url", "This link is not supported",
                 SafeMessage(exception),
                 "Paste a direct link to a video or post, for example https://www.youtube.com/watch?v=... . Site home pages and search pages cannot be extracted.",
                 false),

            ExtractionErrorCode.AuthenticationRequired =>
                (403, "cookies_required", "The platform wants a signed-in session",
                 "YouTube and Instagram reject anonymous requests from most server IP addresses (\"Sign in to confirm you're not a bot\").",
                 $@"Open the desktop app, Settings -> YouTube & Authentication and sign in once (cookies are saved automatically), or export cookies with Tools\Export-BrowserCookies.cmd to {CookieFileHint}. Close the browser first: Chrome and Edge lock their cookie database while running.",
                 true),

            ExtractionErrorCode.MediaUnavailable =>
                (404, "media_unavailable", "The media is not available",
                 SafeMessage(exception),
                 "Confirm the link still opens in a browser and is public (not private, deleted or region locked).",
                 false),

            ExtractionErrorCode.PlatformBlocked =>
                (403, "platform_blocked", "The platform blocked this server",
                 "The site refused the request from this machine's IP address.",
                 $@"Provide session cookies ({CookieFileHint}) or run the server from a different network; a VPN/proxy can be set in the download form.",
                 true),

            ExtractionErrorCode.AdultContentBlocked =>
                (403, "age_restricted", "The media is age restricted",
                 "This item requires an account that is allowed to view age-restricted content.",
                 $@"Sign in through Settings -> YouTube & Authentication so {CookieFileHint} exists, then retry.",
                 false),

            ExtractionErrorCode.NetworkError =>
                (504, "network_error", "The remote platform could not be reached",
                 SafeMessage(exception),
                 "Check the server's internet connection, DNS and proxy settings, then retry.",
                 true),

            _ when LooksLikeDeadLink(exception) =>
                (404, "media_unavailable", "The page or media does not exist",
                 SafeMessage(exception),
                 "Open the link in a browser first: if it 404s or is private, use a public link. Site home pages and search pages cannot be extracted.",
                 false),

            _ when LooksLikeUnsupportedUrl(exception) =>
                (400, "unsupported_url", "This link cannot be extracted",
                 SafeMessage(exception),
                 "Use a direct link to a video or post, not a home page, search page or playlist index.",
                 false),

            _ =>
                (502, "extraction_failed", "The media could not be read",
                 SafeMessage(exception),
                 $@"Retry once. If it keeps failing, confirm FFmpeg is present next to NebulaServer.exe and check the newest log in C:\ProgramData\Tanzeel\Logs.",
                 true)
        };


    /// <summary>
    /// 409 for a URL this server already has. The reason names the existing job
    /// and the fix tells the user (or the UI) how to repeat it deliberately.
    /// </summary>
    public static ApiErrorResponse DuplicateDownload(
        HttpContext context, Services.Jobs.ExistingDownload existing, bool includeDetails)
    {
        var requestId = RequestId(context);
        var when = existing.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        var reason = existing.IsActive
            ? $"\"{existing.Title}\" is already downloading on this server ({existing.Progress:0}% done), started at {when}."
            : $"\"{existing.Title}\" was already downloaded on {when}"
              + (string.IsNullOrWhiteSpace(existing.FileName) ? "." : $" and saved as {existing.FileName}.");

        const string fix = "Confirm to download it again, or open the existing file from the Downloads list. "
                           + "Send the same request with \"AllowDuplicate\": true to skip this check.";

        var message = $"Already downloaded: {reason} Fix: {fix}";

        return new ApiErrorResponse
        {
            Message = message,
            Error = new ApiError
            {
                Code = "duplicate_download",
                Title = existing.IsActive ? "This download is already running" : "This video is already downloaded",
                Reason = reason,
                Fix = fix,
                Status = 409,
                Retryable = true,
                RequestId = requestId,
                Duplicate = new DuplicateDownloadDetail
                {
                    JobId = existing.JobId,
                    Title = existing.Title,
                    FileName = existing.FileName,
                    CreatedAt = existing.CreatedAt,
                    IsActive = existing.IsActive,
                    Progress = existing.Progress
                }
            }
        };
    }

    // =========================================================================
    // Bare status codes (no exception, e.g. auth middleware or routing)
    // =========================================================================

    public static ApiErrorResponse FromStatusCode(
        int statusCode, HttpContext context, string? detail = null, string? fixOverride = null)
    {
        var requestId = RequestId(context);
        var (code, title, reason, fix, retryable) = statusCode switch
        {
            400 => ("bad_request", "The request was rejected",
                detail ?? "The server could not accept this request as sent.",
                "Check the parameters and body against the API table in README.md.", false),

            401 => ("unauthorized", "Pairing key or token required",
                detail ?? "This endpoint is protected; the request carried no valid pairing key or bearer token.",
                @"Send the header X-Nebula-Key with the pairing key (see /pairing), pair the device first, or call from the server itself (loopback).",
                false),

            403 => ("forbidden", "Access denied",
                detail ?? "The pairing key was accepted but this action is not allowed.",
                "Use an account or pairing key that is allowed to perform this action.", false),

            404 => ("not_found", "Endpoint not found",
                detail ?? $"No API route matches {context.Request.Path}.",
                "Check the path and method; the dashboard lives at /app and a health probe at /api/health.", false),

            405 => ("method_not_allowed", "HTTP method not allowed",
                detail ?? $"This endpoint does not accept {context.Request.Method}.",
                "Use the method documented for the route (for example POST /api/jobs to start a download).", false),

            413 => ("payload_too_large", "The request body is too large",
                detail ?? "The uploaded body exceeds the server limit.",
                "Send a smaller body or upload the file through a supported endpoint.", false),

            415 => ("unsupported_media_type", "Unsupported content type",
                detail ?? "The request body was sent with a content type the endpoint does not read.",
                "Send JSON with Content-Type: application/json.", false),

            429 => ("rate_limited", "Too many requests",
                detail ?? "This client exceeded the request limit for the current window.",
                "Wait a few seconds and retry, or raise RateLimiting:PermitLimit in appsettings.json.", true),

            _ => ("server_error", "The request failed",
                detail ?? "The server could not complete the request.",
                $@"Retry once; if it repeats check the newest log in C:\ProgramData\Tanzeel\Logs and search for the request id.",
                true)
        };

        if (!string.IsNullOrWhiteSpace(fixOverride))
            fix = fixOverride;

        return Build(statusCode, code, title, reason, fix, retryable, requestId, null, false);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static ApiErrorResponse Build(
        int status, string code, string title, string reason, string fix, bool retryable,
        string? requestId, Exception? exception, bool includeDetails)
    {
        // The UI renders `message` directly, so keep the actionable part in it.
        var message = string.IsNullOrWhiteSpace(fix) ? $"{title}: {reason}" : $"{title}: {reason} Fix: {fix}";

        return new ApiErrorResponse
        {
            Message = message,
            Error = new ApiError
            {
                Code = code,
                Title = title,
                Reason = reason,
                Fix = fix,
                Status = status,
                Retryable = retryable,
                RequestId = requestId,
                Details = includeDetails && exception is not null
                    ? Truncate(exception.ToString(), 4000)
                    : null
            }
        };
    }

    public static async Task WriteAsync(HttpContext context, ApiErrorResponse payload, int status)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
    }

    public static string? RequestId(HttpContext context) =>
        context.TraceIdentifier is { Length: > 0 } id ? id : null;

    private static readonly string[] Markers =
    {
        "HTTP Error 404", "HTTP Error 410", "Unable to download webpage",
        "video unavailable", "is unavailable", "does not exist", "private video",
        "removed by the uploader", "no longer available", "account associated with this video has been terminated"
    };

    private static bool LooksLikeDeadLink(Exception exception)
    {
        var text = exception.Message ?? string.Empty;
        return Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeUnsupportedUrl(Exception exception)
    {
        var text = exception.Message ?? string.Empty;
        return text.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)
               || text.Contains("is not a valid URL", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? "No further detail was provided." : exception.Message.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public static bool ShouldIncludeDetails(IHostEnvironment environment) =>
        environment.IsDevelopment();

    /// <summary>Only JSON API traffic gets the error envelope; pages keep their own behaviour.</summary>
    public static bool IsApiRequest(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/api")
               || path.StartsWithSegments("/pairing")
               || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Log level per status: client mistakes should not look like server faults.</summary>
    public static LogLevel LevelFor(int status) => status switch
    {
        >= 500 => LogLevel.Error,
        404 or 405 => LogLevel.Debug,
        _ => LogLevel.Warning
    };

    public static int StatusFor(Exception exception) => Describe(exception, new DefaultHttpContext()).Status;
}
