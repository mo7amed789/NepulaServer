using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using NebulaServer.Helpers;
using NebulaServer.Models.Dashboard;
using NebulaServer.Models.Pairing;
using NebulaServer.Services.Pairing;
using NebulaServer.Services.Server;
using QRCoder;
using static NebulaServer.Desktop.DesktopTheme;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm
{
    private readonly Label _runningStat = Label("0", 26, true);
    private readonly Label _queuedStat = Label("0", 26, true);
    private readonly Label _completedStat = Label("0", 26, true);
    private readonly Label _spaceStat = Label("—", 26, true);
    private readonly Label _engineInfo = Label("Checking media engine…");
    private readonly Label _networkInfo = Label("Checking network…");
    private readonly Label _deviceInfo = Label("No connected devices.");
    private readonly PictureBox _pairingQr = new() { Size = new Size(210, 210), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };

    private Control BuildOverviewPage()
    {
        var stack = Stack();
        Control Stat(string title, Label value)
        {
            var card = Card(title); card.Dock = DockStyle.None; card.MinimumSize = new Size(190, 130);
            value.Font = EnterpriseFonts.Create(22, true);
            value.ForeColor = Accent; card.Controls.Add(value); card.Margin = new Padding(0, 0, 16, 12); return card;
        }
        stack.Controls.Add(Row(Stat("Active downloads", _runningStat), Stat("Queued", _queuedStat), Stat("Completed", _completedStat), Stat("Free disk space", _spaceStat)));
        var engine = Card("Media engine"); engine.Controls.Add(_engineInfo); stack.Controls.Add(engine);
        var network = Card("Connect your phone");
        network.Controls.Add(Label("Use the Tanzeel Android app on the same Wi-Fi network. Scan this pairing code.", 10));
        network.Controls.Add(_networkInfo);
        var qrButton = Button("Show pairing code", () => { });
        qrButton.Click += async (_, _) => await BusyAsync(qrButton, async () =>
        {
            var pairing = await _services.GetRequiredService<IPairingService>().GetPairingInfoAsync();
            var state = _services.GetRequiredService<ServerStateService>().Get();
            var payload = new PairingPayload { ServerId = pairing.ServerId, PairingKey = pairing.PairingKey, DeviceId = state.DeviceId, LocalIp = state.LocalIp, Port = state.Port, Version = state.Version };
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(JsonSerializer.Serialize(payload), QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(data);
            using var stream = new MemoryStream(qr.GetGraphic(8));
            using var image = Image.FromStream(stream);
            var old = _pairingQr.Image; _pairingQr.Image = new Bitmap(image); old?.Dispose();
            _pairingQr.Visible = true;
        });
        network.Controls.Add(Row(qrButton));
        _pairingQr.Visible = false;
        network.Controls.Add(_pairingQr); network.Controls.Add(_deviceInfo); stack.Controls.Add(network);
        return ScrollPage(stack);
    }

    private void RenderOverview(DashboardResponse data)
    {
        _runningStat.Text = data.Queue.RunningJobs.ToString();
        _queuedStat.Text = data.Queue.QueuedJobs.ToString();
        _completedStat.Text = _queueRows.Count(r => r.State == "COMPLETED").ToString();
        _spaceStat.Text = $"{data.Storage.FreeSpaceBytes / 1073741824d:0.0} GB";
        _engineInfo.Text = $"Python: {(data.Runtime.PythonReady ? "Ready" : "Starting / unavailable")}     ·     FFmpeg: {(data.Runtime.FfmpegReady ? "Ready" : "Missing")}\nMemory: {data.Performance.MemoryUsageMb:0} MB     ·     CPU: {data.Performance.CpuUsage:0.0}%     ·     Uptime: {data.Server.Uptime:hh\\:mm\\:ss}";
        _networkInfo.Text = $"Local address: {data.Network.LocalUrl}";
        _deviceInfo.Text = data.Devices.Count == 0 ? "No connected devices." : string.Join("\n", data.Devices.Select(d => $"{d.DeviceName} · {d.Platform} · {(d.IsOnline ? "Online" : "Offline")}"));
    }

    private Control BuildSettingsPage()
    {
        var stack = Stack();
        var behavior = Card("Desktop behavior");
        var close = new ToggleSwitch { Text = "Keep running in the system tray when I close the window", Dock = DockStyle.Top, Width = 550, Checked = _preferences.CloseToTray, Margin = new Padding(0, 8, 0, 12) };
        close.CheckedChanged += (_, _) => { _preferences.CloseToTray = close.Checked; SavePreferences(); };
        var notifications = new ToggleSwitch { Text = "Notify me when a download finishes or fails", Dock = DockStyle.Top, Width = 550, Checked = _preferences.NotifyOnCompletion, Margin = new Padding(0, 0, 0, 12) };
        notifications.CheckedChanged += (_, _) => { _preferences.NotifyOnCompletion = notifications.Checked; SavePreferences(); };
        var dark = new ToggleSwitch { Text = "Use dark mode", Dock = DockStyle.Top, Width = 550, Checked = _preferences.DarkMode, Margin = new Padding(0, 0, 0, 12) };
        dark.CheckedChanged += (_, _) => { _preferences.DarkMode = dark.Checked; DesktopTheme.Apply(dark.Checked); ApplyThemeToControls(this); SavePreferences(); };
        var startup = new ToggleSwitch { Text = "Start in the background when Windows starts", Dock = DockStyle.Top, Width = 550, Checked = StartupHelper.IsStartupEnabled(), Margin = new Padding(0, 0, 0, 12) };
        startup.Click += (_, _) =>
        {
            if (!StartupHelper.SetStartup(startup.Checked))
            {
                startup.Checked = StartupHelper.IsStartupEnabled();
                MessageBox.Show(this, "Windows startup could not be updated.", "Tanzeel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        behavior.Controls.Add(close); behavior.Controls.Add(notifications); behavior.Controls.Add(dark); behavior.Controls.Add(startup); stack.Controls.Add(behavior);
        var files = Card("Your files");
        var folder = new TextBox { Text = _storage.DownloadsPath, ReadOnly = true, Dock = DockStyle.Top, AccessibleName = "Downloads folder" };
        files.Controls.Add(Label("Downloads are kept here. Use Save a copy on a completed download to choose another location."));
        files.Controls.Add(folder);
        files.Controls.Add(Row(Button("Open downloads", () => TryAction(() => OpenFolder(_storage.DownloadsPath))), Button("Open logs", () => TryAction(() => OpenFolder(RuntimePaths.Logs)))));
        stack.Controls.Add(files);
        var about = Card("Tanzeel for Windows");
        about.Controls.Add(Label("Video, audio, playlists, and images — in a native Windows app.", 11));
        about.Controls.Add(Label("Ctrl+N  New download     ·     Ctrl+J  Downloads\nDouble-click a completed download to open it with your default media app.", 10));
        about.Controls.Add(Label("Settings are saved automatically. Your existing download history is preserved.", 9));
        stack.Controls.Add(about);
        var page = ScrollPage(stack);
        var sections = Row(Button("Desktop behavior", () => page.ScrollControlIntoView(behavior)), Button("Your files", () => page.ScrollControlIntoView(files)), Button("Shortcuts", () => page.ScrollControlIntoView(about)));
        var layout = new Panel();
        page.Dock = DockStyle.Fill; sections.Dock = DockStyle.Top;
        layout.Controls.Add(page); layout.Controls.Add(sections);
        return layout;
    }
}
