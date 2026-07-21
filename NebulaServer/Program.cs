using NebulaServer.Helpers;
using NebulaServer.Hubs;
using NebulaServer.Middleware;
using NebulaServer.Models.Configuration;
using NebulaServer.Models.Licensing;
using NebulaServer.Models.Pairing;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using NebulaServer.Services.Dashboard;
using NebulaServer.Services.Devices;
using NebulaServer.Services.Jobs;
using NebulaServer.Services.Licensing;
using NebulaServer.Services.Ngrok;
using NebulaServer.Services.Pairing;
using NebulaServer.Services.Server;
using NebulaServer.Settings;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: SupportedOSPlatform("windows")]

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};

var builder = WebApplication.CreateBuilder(options);

#if !DEBUG
if (!builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "NebulaServer Release builds must run with ASPNETCORE_ENVIRONMENT=Production.");
}
#endif

// إعدادات التسجيل (Logging)
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TanzeelServer";
});

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.Configure<NgrokOptions>(
    builder.Configuration.GetSection(NgrokOptions.SectionName));

builder.Services.AddCors(cors =>
{
    cors.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSignalR();
builder.Services.AddSingleton<ServerStateService>();
builder.Services.AddSingleton<ServerStateFileService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<PythonProcessService>();
builder.Services.AddSingleton<JobQueueManager>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<PlaylistEventBus>();
builder.Services.AddSingleton<StreamingTransferService>();
builder.Services.AddSingleton<PublicUrlProvider>();
builder.Services.AddSingleton<NgrokManager>();
builder.Services.AddSingleton<INgrokManager>(sp => sp.GetRequiredService<NgrokManager>());
builder.Services.AddSingleton<SystemBootstrapper>();
builder.Services.AddSingleton<RecoveryBoot>();
builder.Services.AddSingleton<RecoveryStateService>();
builder.Services.AddSingleton<PairingFileService>();
builder.Services.AddSingleton<IPairingService, PairingService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<DeviceRegistryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueueManager>());

var ngrokEnabled = builder.Configuration
    .GetSection("Ngrok")
    .GetValue<bool>("Enabled");

if (ngrokEnabled)
{
    builder.Services.AddHostedService<NgrokWatcher>();
}
builder.Services.AddHostedService<ServerInfoHostedService>();
builder.Services.AddSingleton<IDashboardEvents, DashboardEventService>();
builder.Services.Configure<LicensingOptions>(
    builder.Configuration.GetSection("Licensing"));
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
builder.Services.AddMachineFingerprint();
builder.Services.AddLicensing(builder.Environment.IsProduction());

// =============================================================================
var app = builder.Build();
// =============================================================================

using (var scope = app.Services.CreateScope())
{
    var pairing = scope.ServiceProvider.GetRequiredService<IPairingService>();
    var info = await pairing.GetPairingInfoAsync();

    static string TruncateGuid(Guid value) => value.ToString("N")[..8];
    static string TruncateText(string value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= 8 ? value : $"{value[..4]}…{value[^4..]}";

    Console.WriteLine("======================================");
    Console.WriteLine($"ServerId   : {TruncateGuid(info.ServerId)}");
    Console.WriteLine($"PairingKey : {TruncateText(info.PairingKey)}");
    Console.WriteLine("======================================");
}

app.UseRouting();
app.UseCors("AllowAll");
app.UseMiddleware<PairingAuthenticationMiddleware>();
app.UseMiddleware<LicenseGateMiddleware>();

app.MapHub<DashboardHub>("/dashboardHub");
app.MapControllers();

app.MapGet("/api/system/ready", (ServerStateService serverState, PublicUrlProvider publicUrlProvider) =>
{
    var state = serverState.Get();
    var publicUrl = publicUrlProvider.Get() ?? state.PublicUrl;

    return Results.Ok(new
    {
        status = "ready",
        server = state.IsServerRunning,
        ngrok = state.IsNgrokRunning,
        publicUrl = publicUrl,
        publicUrlReady = publicUrlProvider.IsReady,
        updated = state.LastUpdated
    });
});

// -----------------------------------------------------------------------
// Combined setup / license / pairing page
// -----------------------------------------------------------------------
app.MapGet("/setup", async (HttpContext context, SystemInfoService sysInfo, ILicenseManager licenseManager) =>
{
    var currentIp = sysInfo.GetLocalIpAddress();
    var currentId = sysInfo.DeviceId;
    var currentPort = sysInfo.Port;

    var pairing = await app.Services
        .GetRequiredService<IPairingService>()
        .GetPairingInfoAsync();

    var state = app.Services
        .GetRequiredService<ServerStateService>()
        .Get();

    var payload = new PairingQrPayload
    {
        ServerId = pairing.ServerId,
        PairingKey = pairing.PairingKey,
        DeviceId = sysInfo.DeviceId,
        LocalIp = currentIp,
        Port = currentPort,
        PublicUrl = state.PublicUrl,
        IsNgrokRunning = state.IsNgrokRunning,
        Version = "1.0.0",
        TimestampUtc = DateTime.UtcNow
    };

    var qrPayload = JsonSerializer.Serialize(payload);

    var isLicensed = licenseManager.State == LicenseState.Valid;

    // تحديد حالة قسم الـ Pairing بناءً على الرخصة
    var pairingBoxClass = isLicensed ? "" : "locked-qr";

    context.Response.ContentType = "text/html; charset=utf-8";

    var licenseSection = isLicensed
        ? @"
            <div class='license-status ok'>
                <span class='status-icon'>✓</span> License Active
            </div>"
        : @"
            <div class='license-box'>
                <label for='licenseKey'>License Key</label>
                <input id='licenseKey' type='text' placeholder='NBL-XXXX-XXXX-XXXX-XXXX' autocomplete='off' />
                <button id='activateBtn' onclick='activateLicense()'>Activate License</button>
                <div id='licenseMessage' class='license-message'></div>
            </div>";

    var statusOpacity = isLicensed ? "1" : "0.35";

    // رسالة واتساب مشفرة برمجياً لتكون متوافقة مع الروابط (URL Encoded)
    var whatsappMessage = Uri.EscapeDataString("Hello, I would like to confirm my payment for the Nebula Server Pro subscription (50 L.E). My Server ID is: " + sysInfo.DeviceId);
    var whatsappLink = $"https://wa.me/201500747347?text={whatsappMessage}";

    var html = $@"
    <!DOCTYPE html>
    <html lang='en'>
    <head>
        <meta charset='UTF-8'>
        <meta name='viewport' content='width=device-width, initial-scale=1.0'>
        <title>Nebula Server Pro - Setup</title>
        <script src='https://cdnjs.cloudflare.com/ajax/libs/qrcodejs/1.0.0/qrcode.min.js'></script>
        <style>
            body {{
                background: linear-gradient(#1E2022, #23272A, #2C2F33);
                color: #FFFFFF;
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                display: flex;
                justify-content: center;
                align-items: center;
                min-height: 100vh;
                margin: 0;
            }}
            .card {{
                background: rgba(255, 255, 255, 0.05);
                border: 1px solid rgba(255, 255, 255, 0.12);
                border-radius: 24px;
                padding: 30px;
                width: 580px; /* تم زيادة العرض ليتسع للرمز الكبير */
                text-align: center;
                box-shadow: 0 20px 40px rgba(0,0,0,0.3);
            }}
            h2 {{ margin-top: 0; font-weight: 800; font-size: 24px; color: #E2E8F0; }}
            h3 {{ text-align: left; font-size: 13px; text-transform: uppercase; letter-spacing: 0.08em; color: rgba(255,255,255,0.5); margin: 24px 0 8px; }}
            .license-box {{ text-align: left; }}
            .license-box label {{ font-size: 12px; color: rgba(255,255,255,0.6); display: block; margin-bottom: 6px; }}
            .license-box input {{
                width: 100%;
                box-sizing: border-box;
                padding: 12px 14px;
                border-radius: 10px;
                border: 1px solid rgba(255,255,255,0.15);
                background: rgba(0,0,0,0.25);
                color: #fff;
                font-size: 14px;
                letter-spacing: 0.05em;
                margin-bottom: 12px;
            }}
            .license-box input::placeholder {{ color: rgba(255,255,255,0.3); }}
            .license-box button {{
                width: 100%;
                padding: 12px;
                border: none;
                border-radius: 10px;
                background: #00F5A0;
                color: #0B0E11;
                font-weight: 700;
                font-size: 14px;
                cursor: pointer;
            }}
            .license-box button:disabled {{ opacity: 0.6; cursor: default; }}
            .license-message {{ font-size: 12px; margin-top: 10px; min-height: 16px; }}
            .license-message.error {{ color: #FF6B6B; }}
            .license-message.success {{ color: #00F5A0; }}
            .license-status.ok {{
                display: flex; align-items: center; justify-content: center;
                gap: 8px; font-size: 14px; color: #00F5A0;
                background: rgba(0,245,160,0.08); border: 1px solid rgba(0,245,160,0.25);
                border-radius: 10px; padding: 10px;
            }}
            .status-list {{ text-align: left; margin: 0; padding: 0; list-style: none; opacity: {statusOpacity}; transition: opacity 0.2s ease; }}
            .status-item {{ margin: 10px 0; font-size: 14px; display: flex; align-items: center; }}
            .status-icon {{ color: #00F5A0; margin-right: 10px; font-weight: bold; }}
            
            .qr-section {{
                display: flex;
                justify-content: space-between;
                gap: 15px;
                margin-top: 20px;
                align-items: stretch; /* للمحافظة على تساوي الطول بين الصندوقين */
            }}
            .qr-box {{
                background: #FFFFFF;
                padding: 16px;
                border-radius: 16px;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                flex: 1;
                transition: all 0.3s ease;
            }}
            .qr-box h4 {{
                margin: 0 0 12px 0;
                color: #1E2022;
                font-size: 14px;
                font-weight: 700;
                text-align: center;
                transition: opacity 0.3s ease;
            }}
            .qr-placeholder {{
                display: flex;
                justify-content: center;
                align-items: center;
                transition: all 0.3s ease;
            }}
            
            /* التنسيقات الخاصة بدغوشة الـ Pairing QR */
            .locked-qr h4 {{
                opacity: 0.5;
            }}
            .locked-qr .qr-placeholder {{
                filter: blur(6px);
                opacity: 0.25;
                pointer-events: none;
            }}
            
            /* أزرار الإجراءات للروابط الخارجية */
            .action-buttons {{
                display: flex;
                gap: 8px;
                margin-top: 15px;
                width: 100%;
                justify-content: center;
                flex-wrap: wrap;
            }}
            .action-btn {{
                font-size: 12px;
                text-decoration: none;
                padding: 8px 12px; /* تم تكبير المسافات قليلاً للأزرار */
                border-radius: 6px;
                font-weight: bold;
                display: inline-block;
                transition: opacity 0.2s;
            }}
            .action-btn:hover {{ opacity: 0.8; }}
            .btn-instapay {{ color: #00F5A0; background: #1E2022; }}
            .btn-whatsapp {{ color: #FFFFFF; background: #25D366; }}
            
            .footer-info {{ font-size: 11px; color: rgba(255,255,255,0.4); margin-top: 20px; }}
        </style>
    </head>
    <body>
        <div class='card'>
            <h2>Nebula Server Pro</h2>

            <h3>License</h3>
            {licenseSection}

            <h3>Status</h3>
            <ul class='status-list'>
                <li class='status-item'><span class='status-icon'>✓</span> Nebula Engine: Running</li>
                <li class='status-item'><span class='status-icon'>✓</span> Windows Service: Active (Auto)</li>
                <li class='status-item'><span class='status-icon'>✓</span> Local IP: {currentIp}:{currentPort}</li>
                <li class='status-item'><span class='status-icon'>✓</span> ID: {currentId}</li>
            </ul>

            <div class='qr-section'>
                <div class='qr-box {pairingBoxClass}'>
                    <h4>App Pairing QR</h4>
                    <div class='qr-placeholder' id='qrcode'></div>
                </div>

                <div class='qr-box'>
                    <h4>The subscription is 50 L.E</h4>
                    <div class='qr-placeholder' id='instapayQr'></div>
                    <div class='action-buttons'>
                        <a href='https://ipn.eg/S/mo7amed789/instapay/4N1Ouy' target='_blank' class='action-btn btn-instapay'>Pay via InstaPay</a>
                        <a href='{whatsappLink}' target='_blank' class='action-btn btn-whatsapp'>Share on WhatsApp</a>
                    </div>
                </div>
            </div>

            <div class='footer-info'>Scan QR from Nebula Mobile App to complete pairing instantly</div>
        </div>
        <script>
            // تم تكبير حجم الـ Pairing QR ليكون 200x200
            new QRCode(document.getElementById('qrcode'), {{
                text: '{qrPayload.Replace("\"", "\\\"")}',
                width: 280, 
                height: 280,
                colorDark : '#1E2022',
                colorLight : '#FFFFFF',
                correctLevel: QRCode.CorrectLevel.M
            }});

            // بقي رمز الدفع 150x150 ليوازن الأزرار الموجودة أسفله في الطول
            new QRCode(document.getElementById('instapayQr'), {{
                text: 'https://ipn.eg/S/mo7amed789/instapay/4N1Ouy',
                width: 150,
                height: 150,
                colorDark : '#1E2022',
                colorLight : '#FFFFFF',
                correctLevel: QRCode.CorrectLevel.M
            }});

            async function activateLicense() {{
                var input = document.getElementById('licenseKey');
                var btn = document.getElementById('activateBtn');
                var msg = document.getElementById('licenseMessage');
                var key = input.value.trim();

                if (!key) {{
                    msg.textContent = 'Please enter a license key.';
                    msg.className = 'license-message error';
                    return;
                }}

                btn.disabled = true;
                btn.textContent = 'Activating...';
                msg.textContent = '';
                msg.className = 'license-message';

                try {{
                    var res = await fetch('/api/pairing/license/activate', {{
                        method: 'POST',
                        headers: {{ 'Content-Type': 'application/json' }},
                        body: JSON.stringify({{ licenseKey: key }})
                    }});
                    var data = await res.json();

                    if (data.success) {{
                        msg.textContent = data.message || 'Activated. Reloading...';
                        msg.className = 'license-message success';
                        setTimeout(function() {{ window.location.reload(); }}, 1200);
                    }} else {{
                        msg.textContent = data.message || 'Activation failed.';
                        msg.className = 'license-message error';
                        btn.disabled = false;
                        btn.textContent = 'Activate License';
                    }}
                }} catch (err) {{
                    msg.textContent = 'Network error. Please try again.';
                    msg.className = 'license-message error';
                    btn.disabled = false;
                    btn.textContent = 'Activate License';
                }}
            }}
        </script>
    </body>
    </html>";

    await context.Response.WriteAsync(html);
});
Console.WriteLine(RuntimePaths.Data);
Console.WriteLine(Path.Combine(RuntimePaths.Data, "jobs.db"));

var licenseManager = app.Services.GetRequiredService<ILicenseManager>();

await licenseManager.InitializeAsync();

await app.StartAsync();

if (licenseManager.State != LicenseState.Valid && licenseManager.State != LicenseState.OfflineGrace)
{
    app.Logger.LogWarning(
        "License state is {State}. Core functions are paused. Server is up for activation at /setup...",
        licenseManager.State);
}

_ = Task.Run(async () =>
{
    while (licenseManager.State != LicenseState.Valid && licenseManager.State != LicenseState.OfflineGrace)
    {
        await Task.Delay(1000);
    }

    app.Logger.LogInformation("License is valid or in grace period. Resuming core startup sequence...");

    await app.Services
        .GetRequiredService<RecoveryBoot>()
        .RecoverAsync();

    await app.Services
        .GetRequiredService<SystemBootstrapper>()
        .InitializeAsync();
});

await app.WaitForShutdownAsync();