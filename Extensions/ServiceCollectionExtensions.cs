using System;
using Microsoft.Extensions.DependencyInjection;
using NebulaServer.Helpers;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using NebulaServer.Services.Dashboard;
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

namespace NebulaServer.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddNebulaServices(this IServiceCollection services)
        {
            services.AddControllers();
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
            services.AddSingleton<StorageService>();
            services.AddSingleton<SystemInfoService>();
            services.AddSingleton<PythonProcessService>();
            services.AddSingleton<JobQueueManager>();
            services.AddSingleton<JobStore>();
            services.AddSingleton<PlaylistEventBus>();
            services.AddSingleton<StreamingTransferService>();
            services.AddSingleton<SystemBootstrapper>();
            services.AddSingleton<RecoveryBoot>();
            services.AddSingleton<RecoveryStateService>();
            services.AddSingleton<PairingFileService>();
            services.AddSingleton<IPairingService, PairingService>();
            services.AddSingleton<DashboardService>();
            services.AddSingleton<DeviceRegistryService>();
            services.AddSingleton<IDashboardEvents, DashboardEventService>();
            services.AddSingleton<ServerStateService>();
            services.AddSingleton<ServerStateFileService>();
            services.AddSingleton<LicenseService>();
            services.AddSingleton<ServerInfoHostedService>();

            return services;
        }
    }
}
