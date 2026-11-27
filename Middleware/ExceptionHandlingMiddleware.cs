using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaServer.Services.Errors;

namespace NebulaServer.Middleware;

/// <summary>
/// Final safety net for the whole pipeline. Converts any escaped exception into
/// the uniform error envelope (code, reason and an actionable fix) and logs it
/// once with the same request id the client receives.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-request; nothing to report.
            _logger.LogDebug("Request {Method} {Path} was aborted by the client.",
                context.Request.Method, context.Request.Path);
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var payload = ErrorCatalog.FromException(exception, context, ErrorCatalog.ShouldIncludeDetails(_environment));
        var status = payload.Error.Status;

        if (_environment.IsDevelopment())
            _logger.Log(ErrorCatalog.LevelFor(status),
                exception, "Unhandled {ExceptionType} on {Method} {Path} -> {Status} {Code} (request {RequestId})",
                exception.GetType().Name, context.Request.Method, context.Request.Path,
                status, payload.Error.Code, payload.Error.RequestId);
        else
            _logger.Log(ErrorCatalog.LevelFor(status),
                "Unhandled {ExceptionType} on {Method} {Path} -> {Status} {Code}: {Reason} (request {RequestId})",
                exception.GetType().Name, context.Request.Method, context.Request.Path,
                status, payload.Error.Code, payload.Error.Reason, payload.Error.RequestId);

        if (context.Response.HasStarted)
        {
            // Headers are already on the wire: we cannot rewrite the body.
            _logger.LogWarning("Response for {Path} already started; error envelope could not be sent.",
                context.Request.Path);
            return;
        }

        await ErrorCatalog.WriteAsync(context, payload, status);
    }
}

/// <summary>
/// Shapes empty error responses (routing misses, rejected auth, rate limiting)
/// into the same envelope so clients never have to guess.
/// </summary>
public static class StatusCodeErrorExtensions
{
    public static IApplicationBuilder UseNebulaStatusCodeErrors(this IApplicationBuilder app)
    {
        return app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;

            if (!ErrorCatalog.IsApiRequest(http) || http.Response.HasStarted)
                return;

            // Endpoints that already wrote a body keep their own message.
            if (http.Response.ContentLength is > 0)
                return;

            var status = http.Response.StatusCode;
            if (status < 400)
                return;

            var payload = ErrorCatalog.FromStatusCode(status, http);
            await ErrorCatalog.WriteAsync(http, payload, status);
        });
    }
}
