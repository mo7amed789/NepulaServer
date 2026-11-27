using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NebulaServer.Helpers;
using NebulaServer.Models;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using NebulaServer.Services.Dashboard;
using NebulaServer.Services.Jobs;
using static NebulaServer.Desktop.DesktopTheme;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm : Form
{
    private readonly IServiceProvider _services;
    private readonly JobQueueManager _jobs;
    private readonly JobStore _store;
    private readonly StorageService _storage;
    private readonly DesktopPreferences _preferences = DesktopPreferences.Load();
    private readonly CancellationTokenSource _closing = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _clipboardTimer = new() { Interval = 750 };
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private ToolStripMenuItem _clipboardMenuItem = null!;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(32, 24, 32, 16) };
    private readonly Label _status = Label("Starting media engine…", 9);
    private readonly Label _heading = Label("New download", 25, true);
    private readonly Label _breadcrumb = Label("Workspace / New download", 9);
    private readonly Label _sidebarStatus = Label("●  Engine starting", 9);
    private readonly Label _subtitle = Label("Save video and audio to your computer.");
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, Button> _navigation = new();
    private bool _explicitExit;
    private bool _refreshing;
    private bool _balloonShown;
    private bool _clipboardCheckRunning;
    private string? _lastClipboardText;
    private string _page = "New download";

    public MainForm(IServiceProvider services)
    {
        DesktopTheme.Apply(_preferences.DarkMode);
        _services = services;
        _jobs = services.GetRequiredService<JobQueueManager>();
        _store = services.GetRequiredService<JobStore>();
        _storage = services.GetRequiredService<StorageService>();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = EnterpriseFonts.Create(10);
        Text = "Tanzeel — Media Downloader";
        BackColor = Background;
        ForeColor = Ink;
        MinimumSize = new Size(960, 640);
        var workArea = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1366, 768);
        Size = new Size(Math.Clamp(_preferences.Width, 960, Math.Max(960, workArea.Width)),
            Math.Clamp(_preferences.Height, 640, Math.Max(640, workArea.Height)));
        StartPosition = FormStartPosition.CenterScreen;
        if (_preferences.Maximized) WindowState = FormWindowState.Maximized;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Sidebar, Padding = new Padding(24, 30, 24, 22) };
        var navigation = Stack();
        var brand = Label("↓  Tanzeel", 22, true);
        brand.ForeColor = Color.White;
        var tagline = Label("Media Downloader\n\nWORKSPACE", 9);
        tagline.ForeColor = Color.FromArgb(137, 164, 186);
        tagline.Margin = new Padding(0, 0, 0, 36);
        navigation.Controls.Add(brand);
        navigation.Controls.Add(tagline);
        foreach (var name in new[] { "New download", "Downloads", "Images", "Overview", "Settings" })
        {
            var button = Button(name, () => Navigate(name));
            button.Text = name switch { "New download" => "↓  New download", "Downloads" => "≡  Downloads", "Images" => "▧  Images", "Overview" => "◷  Overview", _ => "⚙  Settings" };
            button.Font = EnterpriseFonts.Create(9, true);
            button.Dock = DockStyle.Top;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.MinimumSize = new Size(188, 48);
            button.Margin = new Padding(0, 0, 0, 8);
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, 20, 20);
            _navigation[name] = button;
            navigation.Controls.Add(button);
        }
        sidebar.Controls.Add(navigation);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 150, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        var shortcutHint = Label("Ctrl+N  New · Ctrl+J  Queue", 8);
        shortcutHint.ForeColor = Color.FromArgb(180, 195, 210); footer.Controls.Add(shortcutHint);
        _sidebarStatus.ForeColor = Color.FromArgb(180, 195, 210);
        footer.Controls.Add(_sidebarStatus);
        var folder = Button("Open downloads", () => TryAction(() => OpenFolder(_storage.DownloadsPath)));
        folder.BackColor = Sidebar; folder.ForeColor = Color.White; folder.FlatAppearance.BorderColor = Color.FromArgb(65, 84, 104);
        footer.Controls.Add(folder);
        var exit = Button("Exit Tanzeel", RequestExit);
        exit.BackColor = Sidebar; exit.ForeColor = Color.FromArgb(180, 195, 210); exit.FlatAppearance.BorderSize = 0;
        footer.Controls.Add(exit);
        sidebar.Controls.Add(footer);

        var main = new Panel { Dock = DockStyle.Fill };
        var header = Stack();
        header.Padding = new Padding(28, 24, 28, 0);
        _subtitle.ForeColor = Muted;
        _breadcrumb.ForeColor = Muted;
        header.Controls.Add(_breadcrumb);
        header.Controls.Add(_heading);
        header.Controls.Add(_subtitle);
        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(28, 8, 12, 0) };
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Muted;
        statusPanel.Controls.Add(_status);
        main.Controls.Add(_content);
        main.Controls.Add(header);
        main.Controls.Add(statusPanel);
        Controls.Add(main);
        Controls.Add(sidebar);

        _pages["New download"] = BuildDownloadPage();
        _pages["Downloads"] = BuildQueuePage();
        _pages["Images"] = BuildImagesPage();
        _pages["Overview"] = BuildOverviewPage();
        _pages["Settings"] = BuildSettingsPage();
        foreach (var page in _pages.Values) { page.Dock = DockStyle.Fill; page.Visible = false; _content.Controls.Add(page); }

        // Controls are initialized before the saved theme is applied. Re-apply
        // colors after the complete visual tree exists so dark startup matches
        // switching dark mode from Settings.
        ApplyThemeToControls(this);
        UpdateLicenseUi(force: true);

        _clipboardMenuItem = new ToolStripMenuItem("Download copied media automatically")
        {
            CheckOnClick = true,
            Checked = _preferences.ClipboardAutoDownloadEnabled
        };
        _clipboardMenuItem.CheckedChanged += (_, _) => SetClipboardAutoDownload(_clipboardMenuItem.Checked);
        _trayMenu.Items.Add(_clipboardMenuItem);
        _trayMenu.Items.Add("Open Tanzeel", null, (_, _) => ShowMainWindow());
        _trayMenu.Items.Add("Open downloads", null, (_, _) => TryAction(() => OpenFolder(_storage.DownloadsPath)));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit Tanzeel", null, (_, _) => RequestExit());
        _tray = new NotifyIcon { Icon = Icon, Text = "Tanzeel — Media Downloader", ContextMenuStrip = _trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.BalloonTipClicked += (_, _) => { ShowMainWindow(); Navigate("Downloads"); };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _clipboardTimer.Tick += async (_, _) => await CheckClipboardAsync();
        _refreshTimer.Start();
        if (_preferences.ClipboardAutoDownloadEnabled) _clipboardTimer.Start();
        Shown += async (_, _) => { _url.Focus(); await RefreshAsync(); };
        Navigate("New download");
        ResumeLayout(true);
    }

    private void ApplyThemeToControls(Control root, bool inSidebar = false)
    {
        BackColor = DesktopTheme.Background;
        ForeColor = DesktopTheme.Ink;
        foreach (Control control in root.Controls)
        {
            var childSidebar = inSidebar || (control is Panel && control.Dock == DockStyle.Left && control.Width <= 240);
            if (control is EnterpriseCard)
                control.BackColor = DesktopTheme.Surface;
            else if (control is Panel or TableLayoutPanel or FlowLayoutPanel)
                control.BackColor = childSidebar ? DesktopTheme.Sidebar : DesktopTheme.Background;
            if (control is Label label)
                label.ForeColor = childSidebar ? (label.Text.Contains("Tanzeel", StringComparison.OrdinalIgnoreCase) ? Color.White : Color.FromArgb(181, 199, 216)) : DesktopTheme.Ink;
            if (control is TextBox textBox) { textBox.BorderStyle = BorderStyle.None; textBox.BackColor = DesktopTheme.Surface; textBox.ForeColor = DesktopTheme.Ink; }
            if (control is ComboBox comboBox) { comboBox.BackColor = DesktopTheme.Surface; comboBox.ForeColor = DesktopTheme.Ink; }
            if (control is NumericUpDown numericUpDown) { numericUpDown.BorderStyle = BorderStyle.None; numericUpDown.BackColor = DesktopTheme.Surface; numericUpDown.ForeColor = DesktopTheme.Ink; }
            if (control is PictureBox pictureBox) { pictureBox.BackColor = DesktopTheme.Surface; }
            if (control is Button button)
            {
                if (childSidebar) { button.BackColor = DesktopTheme.Sidebar; button.ForeColor = Color.FromArgb(181, 199, 216); button.FlatAppearance.BorderColor = Color.FromArgb(65, 84, 104); }
                else if (button.Tag is "primary") { button.BackColor = DesktopTheme.Accent; button.ForeColor = DesktopTheme.Background == Color.Black ? Color.FromArgb(24, 24, 18) : Color.White; button.FlatAppearance.BorderColor = DesktopTheme.Accent; }
                else if (button.Tag is "danger") { button.BackColor = DesktopTheme.Surface; button.ForeColor = Color.Firebrick; button.FlatAppearance.BorderColor = Color.FromArgb(230, 150, 150); }
                else { button.BackColor = DesktopTheme.Surface; button.ForeColor = DesktopTheme.Ink; button.FlatAppearance.BorderColor = DesktopTheme.Border; }
            }
            if (control is DataGridView grid)
            {
                grid.BackgroundColor = DesktopTheme.Surface; grid.GridColor = DesktopTheme.Border;
                grid.ColumnHeadersDefaultCellStyle.BackColor = DesktopTheme.Background;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = DesktopTheme.Muted;
                grid.DefaultCellStyle.ForeColor = DesktopTheme.Ink;
                grid.DefaultCellStyle.SelectionBackColor = DesktopTheme.Accent;
                grid.DefaultCellStyle.SelectionForeColor = Color.White;
            }
            ApplyThemeToControls(control, childSidebar);
        }
        foreach (var (key, button) in _navigation)
        {
            var active = key == _page;
            button.BackColor = active ? (DesktopTheme.Background == Color.Black ? Color.FromArgb(20, 20, 20) : Color.FromArgb(22, 35, 58)) : DesktopTheme.Sidebar;
            button.ForeColor = Color.White;
            button.Tag = active ? "nav-active" : null;
            button.Invalidate();
        }
    }

    private void Navigate(string name)
    {
        _page = name;
        foreach (var (key, page) in _pages) page.Visible = key == name;
        _pages[name].BringToFront();
        foreach (var (key, button) in _navigation)
        {
            button.BackColor = key == name ? (DesktopTheme.Background == Color.Black ? Color.FromArgb(20, 20, 20) : Color.FromArgb(22, 35, 58)) : Sidebar;
            button.ForeColor = key == name ? Color.White : Color.FromArgb(181, 199, 216);
            button.Tag = key == name ? "nav-active" : null;
            button.Invalidate();
        }
        _heading.Text = name;
        _breadcrumb.Text = "Workspace / " + name;
        _subtitle.Text = name switch
        {
            "Downloads" => "Your queue, progress, and saved media in one place.",
            "Images" => "Find images on a page and save the ones you want.",
            "Overview" => "Keep an eye on your media engine and connected devices.",
            "Settings" => "Make Tanzeel work the way you do.",
            _ => "Save video and audio to your computer."
        };
    }

    public void ShowMainWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void OpenYouTubeLogin()
    {
        ShowMainWindow();
        YouTubeCookieService.ShowLoginWindow(this, (success, msg) =>
        {
            if (success)
            {
                _status.Text = "YouTube cookies synchronized successfully!";
                _tray.ShowBalloonTip(2500, "YouTube Authenticated", "Cookies saved. You can now download YouTube media without bot verification.", ToolTipIcon.Info);
                UpdateCookieStatusUi();
            }
        });
    }

    public void Shutdown() { _explicitExit = true; Close(); }

    private void RequestExit()
    {
        if (ConfirmExit()) Shutdown();
    }

    private bool ConfirmExit()
    {
        var stats = _jobs.GetStatistics();
        return stats.RunningJobs + stats.QueuedJobs == 0 || MessageBox.Show(this,
                "Downloads are still active. Exit and stop them? Unfinished jobs will be recovered on the next launch.",
                "Exit Tanzeel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_explicitExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (_preferences.CloseToTray)
            {
                Hide();
                if (!_balloonShown)
                {
                    _tray.ShowBalloonTip(2500, "Tanzeel is still running", "Downloads continue in the background. Open Tanzeel from its tray icon.", ToolTipIcon.Info);
                    _balloonShown = true;
                }
            }
            else if (ConfirmExit()) { _explicitExit = true; e.Cancel = false; }
            if (e.Cancel) return;
        }
        _refreshTimer.Stop();
        _clipboardTimer.Stop();
        _closing.Cancel();
        _preferences.Maximized = WindowState == FormWindowState.Maximized;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _preferences.Width = bounds.Width; _preferences.Height = bounds.Height;
        SavePreferences();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void SetClipboardAutoDownload(bool enabled)
    {
        _preferences.ClipboardAutoDownloadEnabled = enabled;
        SavePreferences();
        _lastClipboardText = null;
        if (enabled)
        {
            _clipboardTimer.Start();
            _tray.ShowBalloonTip(2200, "Clipboard downloads enabled", "Copy a media link to add it to the download queue.", ToolTipIcon.Info);
            _status.Text = "Clipboard downloads enabled.";
        }
        else
        {
            _clipboardTimer.Stop();
            _status.Text = "Clipboard downloads disabled.";
        }
    }

    private async Task CheckClipboardAsync()
    {
        if (_clipboardCheckRunning || !_preferences.ClipboardAutoDownloadEnabled || _closing.IsCancellationRequested)
            return;

        string? clipboardText;
        try
        {
            if (!Clipboard.ContainsText()) return;
            clipboardText = Clipboard.GetText().Trim();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Serilog.Log.Debug(ex, "Clipboard read was unavailable while automatic downloads were enabled.");
            return;
        }

        if (string.Equals(clipboardText, _lastClipboardText, StringComparison.Ordinal))
            return;
        _lastClipboardText = clipboardText;
        if (!DownloadOptions.TryValidateClipboardUrl(clipboardText, out var url))
            return;

        _clipboardCheckRunning = true;
        try
        {
            await QueueClipboardDownloadAsync(url);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Automatic clipboard download failed for {Url}.", url);
            _status.Text = "Clipboard download failed; see the log for details.";
            _tray.ShowBalloonTip(2600, "Clipboard download failed", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _clipboardCheckRunning = false;
        }
    }

    private async Task QueueClipboardDownloadAsync(string url)
    {
        if (!_services.GetRequiredService<LicenseService>().IsActivated)
        {
            _status.Text = "Clipboard download skipped: activate your license first.";
            return;
        }

        if (!_services.GetRequiredService<RecoveryStateService>().Get().IsComplete)
            throw new InvalidOperationException("The media engine is still starting.");

        var request = new JobRequest
        {
            Url = url,
            Format = _preferences.Format,
            Quality = _preferences.Quality,
            AudioQuality = _preferences.AudioQuality,
            FastMode = _preferences.FastMode
        };
        DownloadOptions.Validate(request);
        PreviewData? metadata = null;
        try { metadata = await Task.Run(() => _storage.ExtractPreviewDataAsync(request.Url), _closing.Token); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Clipboard preview extraction failed for {Url}; queuing original URL.", url); }

        if (!ConfirmDuplicateDownload(request))
            return;

        var job = await Task.Run(() => _jobs.CreateJob(request, metadata), _closing.Token);
        _status.Text = "Clipboard media added to the download queue.";
        _tray.ShowBalloonTip(2200, "Download queued", metadata?.Title ?? url, ToolTipIcon.Info);
        Serilog.Log.Information("Queued clipboard media job {JobId} for {Url}.", job.JobId, url);
    }

    private void SavePreferences()
    {
        try { _preferences.Save(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not save desktop preferences"); }
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Serilog.Log.Warning(ex, "Desktop operation failed");
        if (!IsDisposed && !_closing.IsCancellationRequested)
            MessageBox.Show(this, ex.Message, "Tanzeel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task BusyAsync(Control button, Func<Task> action)
    {
        button.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { if (!button.IsDisposed) button.Enabled = true; }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static Panel ScrollPage(Control content)
    {
        var page = new Panel { AutoScroll = true };
        page.Controls.Add(content);
        return page;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N)) { Navigate("New download"); _url.Focus(); return true; }
        if (keyData == (Keys.Control | Keys.J)) { Navigate("Downloads"); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose(); _tray.Dispose(); _trayMenu.Dispose();
            _closing.Cancel(); _closing.Dispose(); _imagePreview.Image?.Dispose(); _pairingQr.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
