using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using NebulaServer.Helpers;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using NebulaServer.Services.Dashboard;
using NebulaServer.Services.Licensing;
using NebulaServer.Services.Devices;
using NebulaServer.Services.Jobs;
using NebulaServer.Services.Pairing;
using NebulaServer.Services.Server;
using NebulaServer.Providers;
using NebulaServer.Services.MediaExtraction;
using NebulaServer.Services.MediaExtraction.Sources;
using NebulaServer.Services.MediaExtraction.Extractors;
using NebulaServer.Services.ImageEngine;
using NebulaServer.Services.ImageEngine.Extractors;
using NebulaServer.Services.Preview;

namespace NebulaServer.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddNebulaServices(this IServiceCollection services)
        {
            services.AddControllers().ConfigureApiBehaviorOptions(options =>
            {
                // Model binding / validation failures must use the same error
                // envelope as exceptions, otherwise the UI gets a bare
                // ProblemDetails blob with no fix instructions.
                options.InvalidModelStateResponseFactory = context =>
                {
                    var problems = context.ModelState
                        .Where(entry => entry.Value?.Errors.Count > 0)
                        .SelectMany(entry => entry.Value!.Errors.Select(error => new
                        {
                            Field = string.IsNullOrWhiteSpace(entry.Key) ? "body" : entry.Key,
                            Message = string.IsNullOrWhiteSpace(error.ErrorMessage)
                                ? "The value is not valid for this field."
                                : error.ErrorMessage
                        }))
                        .ToList();

                    var detail = problems.Count == 0
                        ? "The request body could not be read."
                        : string.Join(" | ", problems.Select(p => $"{p.Field}: {p.Message}"));

                    var contentType = context.HttpContext.Request.ContentType ?? string.Empty;
                    var fix = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                        ? "Send a JSON body with Content-Type: application/json that matches the documented fields, and remove any invalid characters."
                        : "Provide the required query or route values, for example /api/media/extract?url=https://... ; see the API table in README.md.";

                    var payload = NebulaServer.Services.Errors.ErrorCatalog.FromStatusCode(
                        400, context.HttpContext, detail, fix);

                    return new Microsoft.AspNetCore.Mvc.ObjectResult(payload)
                    {
                        StatusCode = StatusCodes.Status400BadRequest
                    };
                };
            });
            services.AddSignalR();
            services.AddHttpClient();

            // Image Engine HttpClients
            services.AddHttpClient("ImageExtractorClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            })
            .ConfigurePrimaryHttpMessageHandler(SafeOutboundHttp.CreateHandler);

            services.AddHttpClient("ImageDownloaderClient", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(2);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            })
            .ConfigurePrimaryHttpMessageHandler(SafeOutboundHttp.CreateHandler);

            // Image Engine Extractors
            services.AddSingleton<DirectImageExtractor>();
            services.AddSingleton<SocialMediaImageExtractor>();
            services.AddSingleton<HtmlImageExtractor>();

            services.AddSingleton<ImageExtractionEngine>(sp =>
            {
                var extractors = new IImageExtractor[]
                {
                    sp.GetRequiredService<DirectImageExtractor>(),
                    sp.GetRequiredService<SocialMediaImageExtractor>(),
                    sp.GetRequiredService<HtmlImageExtractor>()
                };
                return new ImageExtractionEngine(extractors);
            });

            services.AddSingleton<ImageDownloadManager>();

            // New Media Extraction Engine
            services.AddSingleton<ICookieProvider, FileCookieProvider>();
            services.AddSingleton<IInstagramSource, PublicWebSource>();
            services.AddSingleton<IInstagramSource, AuthenticatedSource>();
            services.AddSingleton<IMediaExtractor, InstagramExtractor>();
            services.AddSingleton<IMediaExtractor, UniversalMediaExtractor>();
            services.AddSingleton<MediaExtractionEngine>();

            // Core Services
            services.AddSingleton<MediaPreviewService>();
            services.AddSingleton<SocialMediaMetadataExtractor>();
            services.AddSingleton<StorageService>();
            services.AddSingleton<SystemInfoService>();
            services.AddSingleton<PythonProcessService>();
            services.AddSingleton<JobQueueManager>();
            services.AddSingleton<JobStore>();
            services.AddSingleton<DuplicateDownloadGuard>();
            services.AddSingleton<PlaylistEventBus>();
            services.AddSingleton<StreamingTransferService>();
            services.AddSingleton<RecoveryBoot>();
            services.AddSingleton<RecoveryStateService>();
            services.AddSingleton<PairingFileService>();
            services.AddSingleton<IPairingService, PairingService>();
            services.AddSingleton<DashboardService>();
            services.AddSingleton<DeviceRegistryService>();
            services.AddSingleton<IDashboardEvents, DashboardEventService>();
            services.AddSingleton<ServerStateService>();
            services.AddSingleton<ServerStateFileService>();

            // Licensing link to NebulaLicensingServer (see docs in README).
            services.AddSingleton<IOptions<LicensingOptions>>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var options = configuration.GetSection(LicensingOptions.SectionName).Get<LicensingOptions>()
                              ?? new LicensingOptions();
                return Options.Create(options);
            });
            services.AddSingleton<LicensingClient>();

            services.AddSingleton<LicenseService>();
            services.AddSingleton<ServerInfoHostedService>();
            services.AddHostedService<CoreLifecycleService>();
            services.AddHostedService<LicenseHeartbeatService>();

            return services;
        }
    }
}
