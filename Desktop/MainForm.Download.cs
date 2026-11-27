using System.Windows.Forms;
using NebulaServer.Models;
using NebulaServer.Services;
using NebulaServer.Services.Core;
using static NebulaServer.Desktop.DesktopTheme;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm
{
    private readonly TextBox _url = new() { PlaceholderText = "Paste a video, audio, or playlist link…", Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 16) };
    private readonly ComboBox _format = Choices("mp4", "mp3");
    private readonly ComboBox _quality = Choices("best", "2160", "1440", "1080", "1080p · 60 fps", "1080p · 50 fps", "720", "720p · 60 fps", "720p · 50 fps", "480", "360");
    private readonly ComboBox _audioQuality = Choices("320", "256", "192", "128", "96", "64");
    private readonly CheckBox _fast = new ToggleSwitch { Text = "Fast processing", Dock = DockStyle.Top, Width = 180, Checked = true };
    private readonly CheckBox _playlist = new ToggleSwitch { Text = "Download playlist", Dock = DockStyle.Top, Width = 180 };
    private readonly CheckBox _vocals = new ToggleSwitch { Text = "Extract vocals", Dock = DockStyle.Top, Width = 180 };
    private readonly CheckBox _dataSaver = new ToggleSwitch { Text = "Data saver", Dock = DockStyle.Top, Width = 180 };
    private readonly NumericUpDown _first = new() { Minimum = 1, Maximum = 100000, Value = 1, Enabled = false };
    private readonly NumericUpDown _last = new() { Minimum = 0, Maximum = 100000, Enabled = false };
    private readonly TextBox _clipStart = new() { PlaceholderText = "e.g. 00:30" };
    private readonly TextBox _clipEnd = new() { PlaceholderText = "e.g. 02:00" };
    private readonly TextBox _proxy = new() { PlaceholderText = "Optional proxy URL" };
    private readonly NumericUpDown _speedLimit = new() { Minimum = 0, Maximum = 1000000, Increment = 500 };
    private readonly Label _preview = Label(string.Empty);
    private Button _downloadButton = null!;
    private MediaThumbnail _mediaThumbnail = null!;
    private readonly Label _downloadSummary = Label("Format  mp4\n\nQuality  720\n\nEstimated size  —", 10);
    private readonly FlowLayoutPanel _qualityChips = Row();
    private NebulaServer.Services.PreviewData? _previewData;
    private readonly System.Windows.Forms.Timer _metadataTimer = new() { Interval = 650 };
    private int _previewVersion;

    private Control BuildDownloadPage()
    {
        var stack = Stack();
        var side = Stack();
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        columns.Controls.Add(stack, 0, 0); columns.Controls.Add(side, 1, 0);
        stack.Margin = new Padding(0, 0, 16, 0);
        var source = Card("Add your link");
        _url.AccessibleName = "Media URL";
        source.Controls.Add(_url);
        _url.Font = EnterpriseFonts.Create(9, mono: true);
        _mediaThumbnail = new MediaThumbnail(_services.GetRequiredService<IHttpClientFactory>());
        _url.TextChanged += async (_, _) =>
        {
            _previewVersion++;
            _metadataTimer.Stop();
            _metadataTimer.Start();
            await _mediaThumbnail.LoadAsync("", force: true);
            _previewData = null; UpdateDownloadSummary();
            _preview.Text = _url.Text.Trim().Length == 0 ? string.Empty : "Waiting for link information…";
        };
        _metadataTimer.Tick += async (_, _) =>
        {
            _metadataTimer.Stop();
            await LoadPreviewAsync(_url.Text);
        };
        var paste = Button("Paste link", () => TryAction(() => { if (Clipboard.ContainsText()) _url.Text = Clipboard.GetText().Trim(); }));
        paste.Tag = "primary";
        var preview = Button("Get media info", () => { });
        preview.Click += async (_, _) => await BusyAsync(preview, () => LoadPreviewAsync(_url.Text));
        source.Controls.Add(Row(paste, preview));
        _preview.MaximumSize = new System.Drawing.Size(450, 0);
        _preview.ForeColor = Muted;
        var mediaCard = new TableLayoutPanel { Dock = DockStyle.Top, Height = 160, ColumnCount = 2, BackColor = Background, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
        mediaCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        mediaCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaCard.RowCount = 1;
        mediaCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _mediaThumbnail.Dock = DockStyle.Fill; _mediaThumbnail.Margin = new Padding(0, 0, 12, 0);
        _preview.AutoSize = false; _preview.Dock = DockStyle.Fill; _preview.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        mediaCard.Controls.Add(_mediaThumbnail, 0, 0); mediaCard.Controls.Add(_preview, 1, 0);
        source.Controls.Add(mediaCard);
        stack.Controls.Add(source);

        var options = Card("02   Choose your format");
        if (_format.Items.Contains(_preferences.Format)) _format.SelectedItem = _preferences.Format;
        if (_quality.Items.Contains(_preferences.Quality)) _quality.SelectedItem = _preferences.Quality;
        if (_audioQuality.Items.Contains(_preferences.AudioQuality)) _audioQuality.SelectedItem = _preferences.AudioQuality;
        _fast.Checked = _preferences.FastMode;
        options.Controls.Add(Row(Field("Format", _format, 130), Field("Video quality", _quality, 130), Field("Audio bitrate (kbps)", _audioQuality, 150)));
        options.Controls.Add(_qualityChips);
        foreach (var checkbox in new[] { _fast, _playlist, _vocals, _dataSaver }) checkbox.Margin = new Padding(0, 4, 24, 10);
        var switches = Card("Options");
        foreach (var checkbox in new[] { _fast, _playlist, _vocals, _dataSaver }) switches.Controls.Add(checkbox);
        _format.SelectedIndexChanged += (_, _) => { UpdateFormatControls(); UpdateDownloadSummary(); };
        _quality.SelectedIndexChanged += (_, _) => UpdateDownloadSummary();
        _audioQuality.SelectedIndexChanged += (_, _) => UpdateDownloadSummary();
        _playlist.CheckedChanged += (_, _) => { _first.Enabled = _last.Enabled = _playlist.Checked; };
        UpdateFormatControls();
        var advanced = Stack();
        advanced.Visible = false;
        advanced.AutoSize = false;
        advanced.Height = 0;
        options.Controls.Add(Row(Field("First playlist item", _first, 150), Field("Last item (0 = all)", _last, 150)));
        options.Controls.Add(Label("Playlist range applies when Download playlist is on.", 9));
        advanced.Controls.Add(Row(Field("Clip start", _clipStart), Field("Clip end", _clipEnd)));
        advanced.Controls.Add(Row(Field("Speed limit (KB/s, 0 = none)", _speedLimit, 235), Field("Proxy", _proxy, 320)));
        var advancedToggle = new CheckBox { Text = "Advanced options", AutoSize = true, Margin = new Padding(0, 8, 0, 16) };
        advancedToggle.CheckedChanged += (_, _) =>
        {
            advanced.Visible = advancedToggle.Checked;
            advanced.AutoSize = advancedToggle.Checked;
            advanced.Height = advancedToggle.Checked ? advanced.PreferredSize.Height : 0;
            options.PerformLayout();
            stack.PerformLayout();
            columns.PerformLayout();
        };
        options.Controls.Add(advancedToggle);
        options.Controls.Add(advanced);
        _downloadButton = Button("Start download", () => { }, primary: true);
        _downloadButton.Click += async (_, _) => await BusyAsync(_downloadButton, QueueDownloadAsync);
        var summary = Card("Summary");
        summary.Controls.Add(_downloadSummary);
        summary.Controls.Add(Label("Save to", 9, true));
        summary.Controls.Add(new TextBox { Text = _storage.DownloadsPath, ReadOnly = true, Dock = DockStyle.Top, Font = EnterpriseFonts.Create(8, mono: true), Margin = new Padding(0, 0, 0, 16) });
        summary.Controls.Add(_downloadButton);
        _downloadButton.Dock = DockStyle.Top;
        var enqueue = Button("Add to queue", () => { });
        enqueue.Dock = DockStyle.Top; enqueue.Margin = new Padding(0, 10, 0, 0);
        enqueue.Click += async (_, _) => await BusyAsync(enqueue, async () => { await QueueDownloadAsync(); Navigate("New download"); _status.Text = "Added to your download queue."; });
        summary.Controls.Add(enqueue);
        side.Controls.Add(summary); side.Controls.Add(switches);
        stack.Controls.Add(options);
        var note = Label("Files stay on your computer. Open the Downloads page to view progress or open a saved file.", 9);
        note.ForeColor = Muted;
        stack.Controls.Add(note);
        PopulateQualityChips(); UpdateDownloadSummary();
        var page = ScrollPage(columns);
        bool? narrow = null;
        page.SizeChanged += (_, _) =>
        {
            var next = page.ClientSize.Width < 820;
            if (narrow == next) return;
            narrow = next;
            columns.SuspendLayout();
            columns.SetCellPosition(side, next ? new TableLayoutPanelCellPosition(0, 1) : new TableLayoutPanelCellPosition(1, 0));
            columns.ColumnCount = next ? 1 : 2;
            columns.ColumnStyles.Clear();
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, next ? 100 : 68));
            if (!next) columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            columns.ResumeLayout(true);
        };
        return page;
    }

    private async Task LoadPreviewAsync(string? rawInput)
    {
        var input = rawInput?.Trim() ?? string.Empty;
        if (input.Length < 8) return;

        string url;
        try { url = DownloadOptions.ValidateUrl(input); }
        catch (ArgumentException) { return; }

        var version = ++_previewVersion;
        var proxy = string.IsNullOrWhiteSpace(_proxy.Text) ? null : _proxy.Text.Trim();
        _preview.Text = "Reading media information…";
        try
        {
            var info = await Task.Run(() => _storage.ExtractPreviewDataAsync(url, proxy), _closing.Token);
            if (_closing.IsCancellationRequested || version != _previewVersion || _url.Text.Trim() != input) return;
            _previewData = info;
            _preview.Text = string.IsNullOrWhiteSpace(info.Title) ? "No preview available. You can still try downloading this link." : info.Title;
            if (info.AvailableQualities.Count > 0)
                _preview.Text += "\nAvailable: " + string.Join("  ·  ", info.AvailableQualities.Select(q => q.Label + (q.FilesizeDisplay is null ? "" : $" ({q.FilesizeDisplay})")));
            await _mediaThumbnail.LoadAsync(info.ThumbnailUrl, force: true);
            PopulateQualityChips(); UpdateDownloadSummary();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _previewVersion && !_closing.IsCancellationRequested)
            {
                _previewData = null;
                _preview.Text = "Could not read this link. You can still try the download.";
                Serilog.Log.Debug(ex, "Automatic media preview failed for {Url}.", url);
            }
        }
    }

    private void PopulateQualityChips()
    {
        foreach (Control old in _qualityChips.Controls.Cast<Control>().ToArray()) old.Dispose();
        var qualities = _previewData?.AvailableQualities.Where(q => q.Label != "audio").ToList();
        if (qualities is null || qualities.Count == 0) return;
        foreach (var option in qualities)
        {
            var choice = option.Label;
            if (!_quality.Items.Contains(choice)) _quality.Items.Add(choice);
            var chip = Button(choice + (option.FilesizeDisplay is null ? "" : " · " + option.FilesizeDisplay), () => _quality.SelectedItem = choice);
            chip.MinimumSize = new System.Drawing.Size(70, 34); chip.Font = EnterpriseFonts.Create(9);
            chip.Margin = new Padding(0, 0, 8, 8); _qualityChips.Controls.Add(chip);
        }
    }

    private void UpdateDownloadSummary()
    {
        var option = _previewData?.AvailableQualities.FirstOrDefault(q => q.Label == (_format.Text == "mp3" ? "audio" : _quality.Text));
        _downloadSummary.Text = $"Format     {_format.Text.ToUpperInvariant()}\n\nQuality     {(_format.Text == "mp3" ? _audioQuality.Text + " kbps" : _quality.Text)}\n\nEstimated size     {option?.FilesizeDisplay ?? "—"}";
        _qualityChips.Enabled = _format.Text == "mp4";
    }

    private void UpdateFormatControls()
    {
        _quality.Enabled = _format.Text == "mp4";
        _audioQuality.Enabled = _format.Text == "mp3";
    }

    private async Task QueueDownloadAsync()
    {
        if (!_services.GetRequiredService<RecoveryStateService>().Get().IsComplete)
            throw new InvalidOperationException("The media engine is still starting. Please try again in a moment.");
        var request = new JobRequest
        {
            Url = _url.Text, Format = _format.Text, Quality = _quality.Enabled ? _quality.Text : "best",
            AudioQuality = _audioQuality.Text, FastMode = _fast.Checked, IsPlaylist = _playlist.Checked,
            PlaylistStart = _playlist.Checked ? (int)_first.Value : null,
            PlaylistEnd = _playlist.Checked && _last.Value > 0 ? (int)_last.Value : null,
            SegmentStart = NullIfEmpty(_clipStart.Text), SegmentEnd = NullIfEmpty(_clipEnd.Text),
            ExtractVocals = _vocals.Checked, DataSaverMode = _dataSaver.Checked,
            Proxy = NullIfEmpty(_proxy.Text), MaxSpeedBytes = _speedLimit.Value > 0 ? (long)_speedLimit.Value * 1024 : null
        };
        DownloadOptions.Validate(request);
        PreviewData? metadata = _previewData;
        if (metadata is null)
        {
            try { metadata = await Task.Run(() => _storage.ExtractPreviewDataAsync(request.Url, request.Proxy), _closing.Token); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Metadata prefetch failed; queueing download without preview metadata."); }
        }
        if (!ConfirmDuplicateDownload(request))
            return;

        var job = await Task.Run(() => _jobs.CreateJob(request, metadata), _closing.Token);
        _preferences.Format = _format.Text; _preferences.Quality = _quality.Text;
        _preferences.AudioQuality = _audioQuality.Text; _preferences.FastMode = _fast.Checked;
        SavePreferences();
        _queueFilter.SelectedIndex = 0;
        _queueSearch.Clear();
        Navigate("Downloads");
        await RefreshAsync();
        SelectJob(job.JobId);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
