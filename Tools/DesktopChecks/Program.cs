using System.Drawing.Imaging;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NebulaServer.Desktop;
using NebulaServer.Extensions;
using NebulaServer.Helpers;
using NebulaServer.Models;
using NebulaServer.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "tanzeel-desktop-checks"));
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TANZEEL_DATA_DIR", Path.Combine(output, "state"));
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Reject(new JobRequest { Url = "file:///C:/Windows/notepad.exe" });
            Reject(new JobRequest { Url = "https://example.com/video", SegmentStart = "01:20", SegmentEnd = "00:30" });
            Reject(new JobRequest { Url = "https://example.com/video", SegmentStart = "abc" });
            Reject(new JobRequest { Url = "https://example.com/list", IsPlaylist = true, PlaylistStart = 5, PlaylistEnd = 2 });
            Reject(new JobRequest { Url = "https://example.com/video", Format = "wav" });
            DownloadOptions.Validate(new JobRequest { Url = "https://example.com/video", SegmentStart = "90", SegmentEnd = "02:00", Format = "mp3" });
            var proxyRequest = new JobRequest
            {
                Url = "https://example.com/video",
                Proxy = " https://user:p%40ss@example.net:8443 "
            };
            DownloadOptions.Validate(proxyRequest);
            Check(proxyRequest.Proxy == "https://user:p%40ss@example.net:8443/", "Authenticated HTTP/HTTPS proxy URLs are accepted and normalized");
            Check(DownloadOptions.TryValidateClipboardUrl("https://example.com/video", out var clipboardUrl) && clipboardUrl == "https://example.com/video",
                "Clipboard mode accepts HTTP/HTTPS media links");
            Check(!DownloadOptions.TryValidateClipboardUrl("not a URL", out _),
                "Clipboard mode ignores non-URL text");
            Check(true, "URL, format, clip, and playlist validation");

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5168", ["Storage:KeepDownloads"] = "true" }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddNebulaServices();
            using var provider = services.BuildServiceProvider();
            var storage = provider.GetRequiredService<StorageService>();
            var oldDownload = Path.Combine(storage.DownloadsPath, "keep.mp4");
            File.WriteAllText(oldDownload, "fixture"); File.SetLastWriteTimeUtc(oldDownload, DateTime.UtcNow.AddDays(-3));
            var oldTemp = Path.Combine(storage.TempPath, "expired.tmp");
            File.WriteAllText(oldTemp, "fixture"); File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-3));
            storage.CleanupExpiredArtifacts(TimeSpan.FromDays(1), new HashSet<string>(), keepDownloads: true);
            Check(File.Exists(oldDownload) && !File.Exists(oldTemp), "Saved downloads survive cleanup while temporary artifacts expire");
            var store = provider.GetRequiredService<NebulaServer.Services.Jobs.JobStore>();
            foreach (var state in new[] { "DOWNLOADING", "QUEUED", "COMPLETED", "FAILED" })
                store.Save(new NebulaServer.Models.Jobs.JobSnapshot { Id = "layout-" + state, Url = "https://example.com/media", Status = state, Progress = state == "COMPLETED" ? 100 : state == "DOWNLOADING" ? 62 : 0, OutputPath = state + "-layout-fixture.mp4", CreatedAt = DateTime.UtcNow, RequestJson = "{}" });

            using var form = new MainForm(provider);
            // Render controls inside the test process; no external desktop automation is used.
            _ = form.Handle;
            form.Show();
            Application.DoEvents();
            var navigate = typeof(MainForm).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var thumbnail = (MediaThumbnail)typeof(MainForm).GetField("_mediaThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var loading = thumbnail.LoadAsync("https://i.ytimg.com/vi/vmikbwBYA_I/hqdefault.jpg");
            var timeout = DateTime.UtcNow.AddSeconds(25);
            while (!loading.IsCompleted && DateTime.UtcNow < timeout) { Application.DoEvents(); Thread.Sleep(20); }
            Check(loading.IsCompleted && thumbnail.AccessibleDescription == "Media thumbnail loaded", "Real HTTPS media thumbnail downloaded and decoded");
            var queueRefresh = (Task)typeof(MainForm).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
            while (!queueRefresh.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            foreach (var size in new[] { new Size(1180, 730), new Size(960, 640) })
            {
                form.Size = size;
                foreach (var page in new[] { "New download", "Downloads", "Images", "Overview", "Settings" })
                {
                    navigate.Invoke(form, [page]);
                    form.PerformLayout();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
                    bitmap.Save(Path.Combine(output, $"{page.Replace(' ', '-')}-{size.Width}.png"), ImageFormat.Png);
                }
            }
            var controls = Descendants(form).ToArray();
            Check(!controls.Any(c => c.GetType().Name.Contains("WebView") || c is WebBrowser), "All desktop controls are native; no browser control");
            var format = (ComboBox)typeof(MainForm).GetField("_format", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var quality = (ComboBox)typeof(MainForm).GetField("_quality", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var bitrate = (ComboBox)typeof(MainForm).GetField("_audioQuality", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            format.SelectedItem = "mp3";
            Check(!quality.Enabled && bitrate.Enabled, "MP3 mode enables bitrate and disables video quality");
            format.SelectedItem = "mp4";
            Check(quality.Enabled && !bitrate.Enabled, "MP4 mode enables video quality");
            Check(true, "All five native pages rendered at normal and minimum window sizes");
            Console.WriteLine("Renders: " + output);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Reject(JobRequest request)
    {
        try { DownloadOptions.Validate(request); }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid request was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
