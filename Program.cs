using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.FileProviders;
using NebulaServer.Desktop;
using NebulaServer.Extensions;
using NebulaServer.Helpers;
using NebulaServer.Services;
using NebulaServer.Services.Pairing;
using NebulaServer.Services.Server;
using NebulaServer.Web;
using Serilog;

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

var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
var activationPort = ResolveConfiguredPort();
const string SingleInstanceMutexName = @"Global\Tanzeel_Media_Server_SingleInstance";
var mutexName = SingleInstanceMutexName;
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANZEEL_DATA_DIR")))
{
    var identity = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RuntimePaths.Data.ToUpperInvariant()));
    mutexName += "_" + Convert.ToHexString(identity)[..16];
}
using var singleInstanceMutex = new Mutex(true, mutexName, out var isFirstInstance);
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

    builder.AddNebulaWebServices();

    builder.Services.AddNebulaServices();

    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

    await using var app = builder.Build();
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
    app.MapNebulaEndpoints();
    Console.WriteLine(RuntimePaths.Data);
    Console.WriteLine(Path.Combine(RuntimePaths.Data, "jobs.db"));
    await app.StartAsync();
    if (!headless && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANZEEL_DATA_DIR")) &&
        StartupHelper.RepairStartupRegistrationIfEnabled())
    {
        app.Logger.LogInformation(
            "Updated the Windows startup command to the current executable: {ExecutablePath}",
            StartupHelper.GetExecutablePath());
    }
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

    if (headless)
    {
        await app.WaitForShutdownAsync();
    }
    else
    {
        await DesktopApplication.RunAsync(app.Services, startInBackground, app.Lifetime.ApplicationStopping);
    }

    await app.StopAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled fatal application error.");
    Log.CloseAndFlush();

    if (!startInBackground && !headless)
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

