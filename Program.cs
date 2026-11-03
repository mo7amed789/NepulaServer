using Serilog;
using Photino.NET;
using NebulaServer.Helpers;
using NebulaServer.Hubs;
using NebulaServer.Middleware;
using NebulaServer.Models.Pairing;
using NebulaServer.Extensions;
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
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
using System.Threading.RateLimiting;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QRCoder;

[assembly: SupportedOSPlatform("windows")]

static int ResolveConfiguredPort()
{
    try
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        return Math.Clamp(configuration.GetValue("Server:Port", 5000), 1, 65535);
    }
    catch
    {
        return 5000;
    }
}

var activationPort = ResolveConfiguredPort();
const string SingleInstanceMutexName = @"Global\Tanzeel_Media_Server_SingleInstance";
using var singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
if (!isFirstInstance)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        client.GetAsync($"http://127.0.0.1:{activationPort}/api/system/show").GetAwaiter().GetResult();
    }
    catch
    {
        // Ignore if existing instance API is not ready
    }
    return;
}

var startInBackground = args.Any(a =>
    string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
try
{
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};

RuntimePaths.EnsureDirectories();
var logDir = RuntimePaths.Logs;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "log_.txt"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(options);
builder.Host.UseSerilog();

var hostName = builder.Configuration["Server:Host"] ?? "127.0.0.1";
var configuredPort = Math.Clamp(builder.Configuration.GetValue("Server:Port", 5000), 1, 65535);
var portName = configuredPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
builder.WebHost.UseUrls($"http://{hostName}:{portName}");

builder.WebHost.ConfigureKestrel(kestrelOptions =>
{
    kestrelOptions.Limits.MaxRequestBodySize = 30 * 1024 * 1024; 
});

#if !DEBUG
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsProduction())
{
    builder.Environment.EnvironmentName = Environments.Production;
}
#endif

var jwtSecret = builder.Configuration["Auth:JwtSecret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    var secretPath = Path.Combine(RuntimePaths.Data, "jwt.secret");
    try
    {
        if (File.Exists(secretPath))
        {
            jwtSecret = File.ReadAllText(secretPath).Trim();
        }
        if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
        {
            jwtSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            File.WriteAllText(secretPath, jwtSecret);
        }
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Unable to create or load the signing secret at '{secretPath}'. Configure Auth:JwtSecret securely or grant the application access to its data directory.", ex);
    }

    builder.Configuration["Auth:JwtSecret"] = jwtSecret;
}

if (jwtSecret.Length < 32)
{
    throw new InvalidOperationException("Auth:JwtSecret must contain at least 32 characters.");
}

var key = System.Text.Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwtOptions =>
    {
        jwtOptions.RequireHttpsMetadata = false;
        jwtOptions.SaveToken = true;
        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        
        jwtOptions.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/dashboardHub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownProxies.Clear();
    foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
    {
        if (System.Net.IPAddress.TryParse(value, out var address))
        {
            opts.KnownProxies.Add(address);
        }
    }
});

var permitLimit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 100);
var windowSeconds = builder.Configuration.GetValue<int>("RateLimiting:WindowSeconds", 60);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ?? context.User.FindFirst("sub")?.Value ?? "unknown-user"
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, partition => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds)
        });
    });

    options.AddPolicy("StrictAuth", context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        return RateLimitPartition.GetFixedWindowLimiter(ip, partition => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5)
        });
    });
});

builder.Services.AddNebulaServices();

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var embedJsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.Default
};

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new
    {
        success = false,
        error = "An unexpected server error occurred. Check the server logs for details."
    });
}));
using (var scope = app.Services.CreateScope())
{
    var pairing = scope.ServiceProvider.GetRequiredService<IPairingService>();
    var systemInfo = scope.ServiceProvider.GetRequiredService<SystemInfoService>();
    var serverState = scope.ServiceProvider.GetRequiredService<ServerStateService>();
    serverState.Update(state =>
    {
        state.DeviceId = systemInfo.DeviceId;
        state.LocalIp = systemInfo.GetLocalIpAddress();
        state.Port = systemInfo.Port;
        state.IsServerRunning = true;
        state.Version = systemInfo.Version;
    });
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

app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseRouting();
app.UseRateLimiter();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "Html")),
    RequestPath = "/static"
});
// Authentication is intentionally disabled for this local/LAN deployment.
// Every API route is reachable without bearer tokens or pairing headers.
var appPagePath = Path.Combine(AppContext.BaseDirectory, "Html", "app.min.html");
var imagesPagePath = Path.Combine(AppContext.BaseDirectory, "Html", "images.min.html");
app.MapHub<DashboardHub>("/dashboardHub");
app.MapControllers();

// Open the main application directly.  Keep the legacy setup URL as a
// redirect so old shortcuts do not send users back to the license page.
app.MapGet("/", () => Results.Redirect("/app"));
app.MapGet("/setup", () => Results.Redirect("/app"));

app.MapGet("/app", async (CancellationToken ct) =>
{
    var html = await File.ReadAllTextAsync(appPagePath, ct);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/images", async (CancellationToken ct) =>
{
    var html = await File.ReadAllTextAsync(imagesPagePath, ct);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/pairing", async (ServerStateService serverState, IPairingService pairingService) =>
{
    var state = serverState.Get();
    var pairing = await pairingService.GetPairingInfoAsync();
    var payload = new PairingPayload
    {
        ServerId = pairing.ServerId,
        PairingKey = pairing.PairingKey,
        DeviceId = state.DeviceId,
        LocalIp = state.LocalIp,
        Port = state.Port,
        Version = state.Version
    };

    var json = JsonSerializer.Serialize(payload);
    using var qrGenerator = new QRCodeGenerator();
    using var qrData = qrGenerator.CreateQrCode(json, QRCodeGenerator.ECCLevel.Q);
    using var qr = new PngByteQRCode(qrData);
    var qrBase64 = Convert.ToBase64String(qr.GetGraphic(20,
        new byte[] { 255, 255, 255, 255 }, new byte[] { 30, 30, 30, 255 }));
    var safeJson = System.Net.WebUtility.HtmlEncode(json);

    var pairingTemplatePath = Path.Combine(AppContext.BaseDirectory, "Html", "pairing.html");
    var html = await File.ReadAllTextAsync(pairingTemplatePath);
    html = html.Replace("__QR_BASE64__", qrBase64, StringComparison.Ordinal)
        .Replace("__PAIRING_JSON__", safeJson, StringComparison.Ordinal)
        .Replace("__LOCAL_IP__", System.Net.WebUtility.HtmlEncode(state.LocalIp), StringComparison.Ordinal)
        .Replace("__PORT__", state.Port.ToString(), StringComparison.Ordinal)
        .Replace("__VERSION__", System.Net.WebUtility.HtmlEncode(state.Version), StringComparison.Ordinal);
    return Results.Content(html, "text/html; charset=utf-8");
});
app.MapGet("/api/system/ready", (ServerStateService serverState) =>
{
    var state = serverState.Get();

    return Results.Ok(new
    {
        status = "ready",
        server = state.IsServerRunning,
        updated = state.LastUpdated
    });
});
app.MapGet("/api/setup/status", async (
    HttpContext context,
    SystemInfoService sysInfo,
    ServerStateService serverState,
    IPairingService pairingService,
    CancellationToken ct) =>
{
    var pairing = await pairingService.GetPairingInfoAsync();
    var state = serverState.Get();

    var qrPayload = new PairingQrPayload
    {
        ServerId = pairing.ServerId,
        PairingKey = pairing.PairingKey,
        LocalIp = sysInfo.GetLocalIpAddress(),
        Port = sysInfo.Port,
    };
    return Results.Json(new
    {
        localIp = sysInfo.GetLocalIpAddress(),
        deviceId = sysInfo.DeviceId,
        port = sysInfo.Port,
        qrPayload
    }, embedJsonOptions, contentType: "application/json", statusCode: 200);
});
app.MapPost("/api/setup/license", async (
    HttpContext context,
    LicenseService licenseService,
    ServerStateService serverState,
    ServerStateFileService fileService,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync(ct);
    
    var data = JsonSerializer.Deserialize<JsonElement>(body);
    if (!data.TryGetProperty("licenseKey", out var keyProp))
    {
        return Results.BadRequest(new { error = "Missing licenseKey" });
    }
    
    var licenseKey = keyProp.GetString();
    if (string.IsNullOrWhiteSpace(licenseKey))
    {
        return Results.BadRequest(new { error = "License key cannot be empty" });
    }
    
    var isValid = await licenseService.ValidateLicenseAsync(licenseKey);
    if (!isValid)
    {
        return Results.BadRequest(new { error = "Invalid license key" });
    }
    
    serverState.Update(state =>
    {
        state.IsLicensed = true;
        state.LicenseKey = licenseKey;
    });
    
    await fileService.SaveAsync(serverState.Get());
    
    return Results.Ok(new { success = true });
});
Console.WriteLine(RuntimePaths.Data);
Console.WriteLine(Path.Combine(RuntimePaths.Data, "jobs.db"));
await app.StartAsync();
if (StartupHelper.RepairStartupRegistrationIfEnabled())
{
    app.Logger.LogInformation(
        "Updated the Windows startup command to the current executable: {ExecutablePath}",
        StartupHelper.GetExecutablePath());
}
_ = Task.Run(async () =>
{
    var stoppingToken = app.Lifetime.ApplicationStopping;

    try
    {

        await app.Services
            .GetRequiredService<RecoveryBoot>()
            .RecoverAsync();

        await app.Services
            .GetRequiredService<SystemBootstrapper>()
            .InitializeAsync();

        app.Logger.LogInformation("Starting core background services...");

        var jobQueue = app.Services.GetRequiredService<JobQueueManager>();
        // IHostedService.StartAsync requires standard execution
        await ((Microsoft.Extensions.Hosting.IHostedService)jobQueue).StartAsync(stoppingToken);

        var serverInfoSvc = app.Services.GetRequiredService<ServerInfoHostedService>();
        await ((Microsoft.Extensions.Hosting.IHostedService)serverInfoSvc).StartAsync(stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        app.Logger.LogInformation("Shutdown requested; abandoning startup sequence.");
    }
});

var initialState = await app.Services.GetRequiredService<ServerStateFileService>().LoadAsync();
var isLicensed = initialState?.IsLicensed ?? false;
if (initialState != null)
{
    app.Services.GetRequiredService<ServerStateService>().Update(s =>
    {
        s.IsLicensed = initialState.IsLicensed;
        s.LicenseKey = initialState.LicenseKey;
    });
}
// License activation is no longer a startup gate; the desktop app opens the
// main dashboard on every launch. API authentication is intentionally disabled.
var initialRoute = "/app";
var mainWindowUrl = $"http://127.0.0.1:{portName}{initialRoute}";

// Launch Photino Window on an STA thread (required for WebView2 on Windows)
var windowThread = new Thread(() =>
{
    PhotinoWindow? window = null;
    NotifyIcon? trayIcon = null;
    var isExplicitExit = false;

    void ShowMainWindow()
    {
        if (window != null && window.WindowHandle != IntPtr.Zero)
        {
            window.Invoke(() =>
            {
                Program.ShowWindow(window.WindowHandle, Program.SW_SHOW);
                Program.ShowWindow(window.WindowHandle, Program.SW_RESTORE);
                Program.SetForegroundWindow(window.WindowHandle);

                // WebView2 can create a blank surface when it is initialized
                // hidden during Windows sign-in. Navigate again only when the
                // user actually opens the window, after it is visible.
                window.Load($"{mainWindowUrl}?desktop={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

                try
                {
                    Program.EnumChildWindows(window.WindowHandle, (hWndChild, lParam) =>
                    {
                        Program.ShowWindow(hWndChild, Program.SW_SHOW);
                        return true;
                    }, IntPtr.Zero);

                    Program.SetWindowPos(window.WindowHandle, IntPtr.Zero, 0, 0, 0, 0,
                        Program.SWP_NOMOVE | Program.SWP_NOSIZE | Program.SWP_NOZORDER | Program.SWP_FRAMECHANGED | Program.SWP_SHOWWINDOW);

                    var curSize = window.Size;
                    if (curSize.Width > 0 && curSize.Height > 0)
                    {
                        window.SetSize(curSize.Width, curSize.Height + 1);
                        window.SetSize(curSize.Width, curSize.Height);
                    }

                    Program.RedrawWindow(window.WindowHandle, IntPtr.Zero, IntPtr.Zero,
                        Program.RDW_INVALIDATE | Program.RDW_ALLCHILDREN | Program.RDW_UPDATENOW | Program.RDW_ERASE);
                }
                catch { }
            });
        }
    }

    Program.ShowWindowAction = () =>
    {
        ShowMainWindow();
    };

    try
    {
        window = new PhotinoWindow()
            .SetTitle("Tanzeel")
            .SetUseOsDefaultSize(false)
            .SetSize(new System.Drawing.Size(1024, 768))
            .Center();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                window.SetIconFile(iconPath);
            }
            catch { }
        }

        window.RegisterWindowCreatedHandler((s, e) =>
        {
            if (startInBackground)
            {
                Program.ShowWindow(window.WindowHandle, Program.SW_HIDE);
            }
        });

        window.RegisterWindowClosingHandler((s, e) =>
        {
            if (!isExplicitExit)
            {
                Program.ShowWindow(window.WindowHandle, Program.SW_HIDE);
                try
                {
                    trayIcon?.ShowBalloonTip(
                        2500,
                        "Tanzeel Media Server",
                        "Tanzeel is running in the background. Right-click or double-click this icon to open.",
                        ToolTipIcon.Info
                    );
                }
                catch { }
                return true; // Cancel close, minimize to tray
            }
            return false; // Real exit
        });

        // Setup Windows System Tray Icon
        trayIcon = new NotifyIcon();
        if (File.Exists(iconPath))
        {
            try
            {
                trayIcon.Icon = new System.Drawing.Icon(iconPath);
            }
            catch
            {
                trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }
        else
        {
            trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        trayIcon.Text = "Tanzeel Media Server (Running)";
        trayIcon.Visible = true;

        var contextMenu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open Tanzeel");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (s, e) => ShowMainWindow();

        var browserItem = new ToolStripMenuItem("Open in Browser");
        browserItem.Click += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = mainWindowUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        };

        var startupItem = new ToolStripMenuItem("Start with Windows");
        startupItem.Checked = StartupHelper.IsStartupEnabled();
        startupItem.Click += (s, e) =>
        {
            var newState = !startupItem.Checked;
            if (StartupHelper.SetStartup(newState))
            {
                startupItem.Checked = newState;
            }
        };

        var exitItem = new ToolStripMenuItem("Exit Tanzeel");
        exitItem.Click += (s, e) =>
        {
            isExplicitExit = true;
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            window.Close();
        };

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(browserItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(startupItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        trayIcon.ContextMenuStrip = contextMenu;
        trayIcon.DoubleClick += (s, e) => ShowMainWindow();

        window.Load(mainWindowUrl);
        window.WaitForClose();
    }
    finally
    {
        if (trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }
    }
});
windowThread.SetApartmentState(ApartmentState.STA);
windowThread.Start();
windowThread.Join();

await app.StopAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled fatal application error.");
    Log.CloseAndFlush();

    if (!startInBackground)
    {
        MessageBox.Show(
            $"Tanzeel Media Server failed to start:\n\n{ex.Message}\n\nCheck logs at:\n{Path.Combine(RuntimePaths.Logs, "log_.txt")}",
            "Tanzeel Media Server — Startup Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program
{
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    public static Action? ShowWindowAction;

    public static void RequestShowWindow()
    {
        ShowWindowAction?.Invoke();
    }
}
