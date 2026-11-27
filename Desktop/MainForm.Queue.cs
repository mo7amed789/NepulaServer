using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;
using NebulaServer.Services.Dashboard;
using static NebulaServer.Desktop.DesktopTheme;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm
{
    private readonly DataGridView _queueGrid = Grid(("Title", "MEDIA", 36), ("State", "STATUS", 15), ("Progress", "PROGRESS", 15), ("Speed", "SPEED", 11), ("Eta", "ETA", 8), ("Date", "ADDED", 15));
    private readonly ComboBox _queueFilter = Choices("All downloads", "Active", "Completed", "Failed / canceled");
    private readonly TextBox _queueSearch = new() { PlaceholderText = "Search title or URL…", Width = 260 };
    private readonly Label _queueSummary = Label("No downloads yet", 11, true);
    private readonly TextBox _jobDetails = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = System.Drawing.Color.White, ScrollBars = ScrollBars.Vertical };
    private readonly Label _emptyQueue = Label("Your downloads will appear here.\nChoose New download to add your first link.", 12);
    private List<QueueRow> _queueRows = new();
    // History is only re-read from SQLite when the store revision changes;
    // live states overlay from the in-memory queue on every refresh.
    private List<JobSnapshot>? _historyCache;
    private long _historyCacheRevision = -1;
    private readonly Dictionary<string, string> _lastStates = new();
    private MediaThumbnail _selectedThumbnail = null!;
    private Button _openJob = null!, _revealJob = null!, _saveJob = null!, _cancelJob = null!, _retryJob = null!, _copyUrl = null!;

    private sealed record QueueRow(JobSnapshot Snapshot, string Title, string State, string Progress, string Speed, string Eta, string Message);

    private Control BuildQueuePage()
    {
        var layout = new TableLayoutPanel { RowCount = 4, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        _queueFilter.Width = 180;
        _queueFilter.Items.Add("Queued");
        _queueGrid.RowTemplate.Height = 60;
        _queueGrid.CellPainting += PaintQueueCell;
        var clearHistory = Button("Delete history", () => { });
        clearHistory.Tag = "danger";
        clearHistory.Click += async (_, _) => await BusyAsync(clearHistory, ClearDownloadHistoryAsync);
        clearHistory.ForeColor = Color.Firebrick;
        clearHistory.FlatAppearance.BorderColor = Color.FromArgb(230, 190, 190);
        layout.Controls.Add(Row(_queueFilter, _queueSearch, Button("Refresh", async () => await RefreshAsync()), clearHistory));
        layout.Controls.Add(_queueSummary);
        var gridPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        gridPanel.Controls.Add(_queueGrid);
        _emptyQueue.Dock = DockStyle.Fill; _emptyQueue.TextAlign = ContentAlignment.MiddleCenter;
        _emptyQueue.AutoSize = false; _emptyQueue.ForeColor = Muted;
        gridPanel.Controls.Add(_emptyQueue);
        layout.Controls.Add(gridPanel);
        var details = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12) };
        _selectedThumbnail = new MediaThumbnail(_services.GetRequiredService<IHttpClientFactory>()) { Dock = DockStyle.Left, Width = 150 };
        var cancel = Button("Cancel", () => { });
        cancel.Click += async (_, _) => await BusyAsync(cancel, async () =>
        {
            var row = SelectedJob();
            if (!await _jobs.CancelJobAsync(row.Snapshot.Id)) throw new InvalidOperationException("This download has already finished.");
            await RefreshAsync();
        });
        var retry = Button("Retry", () => { });
        retry.Click += async (_, _) => await BusyAsync(retry, async () =>
        {
            var row = SelectedJob();
            if (row.State is not ("FAILED" or "CANCELED")) throw new InvalidOperationException("Only failed or canceled downloads can be retried.");
            var request = JsonSerializer.Deserialize<JobRequest>(row.Snapshot.RequestJson) ?? throw new InvalidOperationException("The original download options are missing.");
            DownloadOptions.Validate(request);
            request.AllowDuplicate = true;   // retrying a failed job is already explicit
            var job = await Task.Run(() => _jobs.CreateJob(request), _closing.Token);
            _queueFilter.SelectedIndex = 0;
            await RefreshAsync(); SelectJob(job.JobId);
        });
        _copyUrl = Button("Copy URL", () => TryAction(CopySelectedUrl));
        var save = Button("Save a copy…", () => { });
        save.Click += async (_, _) => await BusyAsync(save, SaveSelectedAsync);
        _openJob = Button("Open file", () => TryAction(OpenSelected));
        _revealJob = Button("Show in folder", () => TryAction(RevealSelected));
        _saveJob = save; _cancelJob = cancel; _retryJob = retry;
        var actions = Row(_copyUrl, _openJob, _revealJob, save, cancel, retry);
        actions.Dock = DockStyle.Bottom;
        details.Controls.Add(_jobDetails); details.Controls.Add(_selectedThumbnail); details.Controls.Add(actions);
        layout.Controls.Add(details);
        _queueGrid.SelectionChanged += (_, _) => UpdateJobDetails();
        _queueGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) TryAction(OpenSelected); };
        _queueSearch.TextChanged += (_, _) => RenderQueue();
        _queueFilter.SelectedIndexChanged += (_, _) => RenderQueue();
        return layout;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _closing.IsCancellationRequested) return;
        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var dashboard = _services.GetRequiredService<DashboardService>().GetDashboard();
                var revision = _store.HistoryRevision;
                if (_historyCache is null || revision != _historyCacheRevision)
                {
                    _historyCache = _store.GetAllHistory();
                    _historyCacheRevision = revision;
                }
                var rows = _historyCache.Select(saved =>
                {
                    var live = _jobs.GetJob(saved.Id);
                    var title = live?.OriginalTitle;
                    if (string.IsNullOrWhiteSpace(title)) title = saved.Title;
                    if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileName(saved.OutputPath);
                    if (string.IsNullOrWhiteSpace(title)) title = saved.Url;
                    return new QueueRow(saved, title, live?.State.ToString() ?? saved.Status,
                        live?.ProgressPercentage ?? $"{saved.Progress:0.#}%", live?.Speed ?? "", live?.Eta ?? "", live?.Message ?? "");
                }).ToList();
                return (dashboard, rows);
            }, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            _queueRows = snapshot.rows;
            foreach (var row in _queueRows)
            {
                if (_lastStates.TryGetValue(row.Snapshot.Id, out var previous) && previous != row.State &&
                    row.State is "COMPLETED" or "FAILED" && _preferences.NotifyOnCompletion)
                    _tray.ShowBalloonTip(3500, row.State == "COMPLETED" ? "Download complete" : "Download failed",
                        row.Title.Length > 180 ? row.Title[..180] : row.Title, row.State == "COMPLETED" ? ToolTipIcon.Info : ToolTipIcon.Warning);
                _lastStates[row.Snapshot.Id] = row.State;
            }
            RenderQueue();
            RenderOverview(snapshot.dashboard);
            var runtime = snapshot.dashboard.Runtime;
            _sidebarStatus.Text = runtime.PythonReady ? "●  Engine ready" : "●  Engine starting";
            _status.Text = $"{(runtime.PythonReady ? "●  Engine ready" : "◌  Engine starting")}     ·     {snapshot.dashboard.Queue.RunningJobs} active     ·     {snapshot.dashboard.Queue.QueuedJobs} queued     ·     {snapshot.dashboard.Storage.FreeSpaceBytes / 1073741824d:0.0} GB free";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "Could not refresh status. Retrying…";
            Serilog.Log.Warning(ex, "Desktop refresh failed");
        }
        finally { _refreshing = false; }
    }

    private void RenderQueue()
    {
        var selectedId = (_queueGrid.CurrentRow?.Tag as QueueRow)?.Snapshot.Id;
        var query = _queueSearch.Text.Trim();
        var rows = _queueRows.Where(row =>
            (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Snapshot.Url.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (_queueFilter.SelectedIndex switch
            {
                1 => row.State is not ("COMPLETED" or "FAILED" or "CANCELED"),
                2 => row.State == "COMPLETED", 3 => row.State is "FAILED" or "CANCELED", 4 => row.State == "QUEUED", _ => true
            })).ToList();
        // Update stable rows in place, preserving keyboard selection and scroll position.
        var sameRows = _queueGrid.Rows.Count == rows.Count && rows.Select(r => r.Snapshot.Id)
            .SequenceEqual(_queueGrid.Rows.Cast<DataGridViewRow>().Select(r => (r.Tag as QueueRow)?.Snapshot.Id));
        if (!sameRows) _queueGrid.Rows.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            object[] values = [row.Title, row.State.Replace('_', ' '), row.Progress, row.Speed, row.Eta, row.Snapshot.CreatedAt.ToLocalTime().ToString("MMM d, HH:mm")];
            DataGridViewRow gridRow;
            if (sameRows) { gridRow = _queueGrid.Rows[index]; gridRow.SetValues(values); }
            else gridRow = _queueGrid.Rows[_queueGrid.Rows.Add(values)];
            gridRow.Tag = row;
            gridRow.Cells[0].ToolTipText = row.Snapshot.Url;
            gridRow.Cells[5].ToolTipText = row.Snapshot.CreatedAt.ToLocalTime().ToString("f");
            gridRow.Cells[1].Style.ForeColor = row.State switch { "COMPLETED" => Accent, "FAILED" => Color.Firebrick, _ => Ink };
        }
        _emptyQueue.Visible = rows.Count == 0;
        _emptyQueue.Text = _queueRows.Count == 0 ? "Your downloads will appear here.\nChoose New download to add your first link." : "No downloads match this filter.";
        _queueSummary.Text = $"{rows.Count} downloads  ·  {_queueRows.Count(r => r.State == "COMPLETED")} completed";
        if (!sameRows && selectedId != null) SelectJob(selectedId);
        UpdateJobDetails();
    }

    private void SelectJob(string id)
    {
        foreach (DataGridViewRow row in _queueGrid.Rows)
            if ((row.Tag as QueueRow)?.Snapshot.Id == id) { _queueGrid.CurrentCell = row.Cells[0]; return; }
    }

    private QueueRow SelectedJob() => _queueGrid.CurrentRow?.Tag as QueueRow ?? throw new InvalidOperationException("Select a download first.");

    private async Task ClearDownloadHistoryAsync()
    {
        var terminalCount = _queueRows.Count(row => row.State is "COMPLETED" or "FAILED" or "CANCELED");
        if (terminalCount == 0)
        {
            _status.Text = "There is no completed or failed history to delete.";
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete {terminalCount} saved history item{(terminalCount == 1 ? "" : "s")}?\r\n\r\nDownloaded files will stay on disk. Active downloads will remain in the queue.",
            "Delete download history", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        await Task.Run(() => _store.ClearHistory(), _closing.Token);
        _status.Text = "Download history deleted. Saved files were kept.";
        await RefreshAsync();
    }

    private void UpdateJobDetails()
    {
        var row = _queueGrid.CurrentRow?.Tag as QueueRow;
        if (_openJob != null)
        {
            _openJob.Enabled = _revealJob.Enabled = _saveJob.Enabled = row?.State == "COMPLETED";
            _copyUrl.Enabled = row != null;
            _cancelJob.Enabled = row != null && row.State is not ("COMPLETED" or "FAILED" or "CANCELED");
            _retryJob.Enabled = row?.State is "FAILED" or "CANCELED";
        }
        _jobDetails.Text = row == null ? "Select a download to see its details." :
            $"{row.Title}\r\n\r\nURL\r\n{row.Snapshot.Url}\r\n\r\nStatus     {row.State.Replace('_', ' ')}\r\nProgress   {row.Progress}" +
            (string.IsNullOrWhiteSpace(row.Message) ? string.Empty : $"\r\n\r\n{row.Message}");
        if (_selectedThumbnail != null)
        {
            var thumbnail = row == null ? null : _jobs.GetJob(row.Snapshot.Id)?.ThumbnailUrl;
            if (string.IsNullOrWhiteSpace(thumbnail)) thumbnail = row?.Snapshot.ThumbnailUrl;
            _ = _selectedThumbnail.LoadAsync(thumbnail ?? "");
        }
    }

    private void CopySelectedUrl()
    {
        var row = SelectedJob();
        Clipboard.SetText(row.Snapshot.Url);
        _status.Text = "Video URL copied to the clipboard.";
    }

    private void PaintQueueCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex is not (1 or 2)) return;
        var row = _queueGrid.Rows[e.RowIndex].Tag as QueueRow;
        if (row is null) return;
        e.PaintBackground(e.ClipBounds, true);
        var color = row.State switch { "COMPLETED" => Color.FromArgb(5, 150, 105), "FAILED" => Color.FromArgb(220, 38, 38), "QUEUED" or "CANCELED" => Muted, _ => Color.FromArgb(37, 99, 235) };
        if (e.ColumnIndex == 1)
        {
            var bounds = Rectangle.Inflate(e.CellBounds, -7, -17);
            using var path = EnterpriseCard.Rounded(bounds, 9);
            using var tint = new SolidBrush(Color.FromArgb(235 + color.R / 13, 235 + color.G / 13, 235 + color.B / 13));
            e.Graphics!.FillPath(tint, path);
            TextRenderer.DrawText(e.Graphics, row.State.Replace('_', ' '), e.CellStyle!.Font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            double.TryParse(row.Progress.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var progress);
            var bounds = new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y + 20, Math.Max(8, e.CellBounds.Width - 16), 5);
            e.Graphics!.FillRectangle(Brushes.Gainsboro, bounds);
            using var fill = new SolidBrush(color);
            e.Graphics.FillRectangle(fill, bounds.X, bounds.Y, (float)(bounds.Width * Math.Clamp(progress, 0, 100) / 100), bounds.Height);
            TextRenderer.DrawText(e.Graphics, row.Progress, e.CellStyle!.Font, new Rectangle(bounds.X, bounds.Bottom + 3, bounds.Width, 22), Ink, TextFormatFlags.HorizontalCenter);
        }
        e.Handled = true;
    }

    private string SelectedPath(QueueRow row)
    {
        var playlistPath = _storage.GetPlaylistDirectoryPath(row.Snapshot.Id);
        if (Directory.Exists(playlistPath)) return playlistPath;
        var path = row.Snapshot.OutputPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Path.GetFullPath(path);
        var live = _jobs.GetJob(row.Snapshot.Id);
        if (!string.IsNullOrWhiteSpace(live?.OutputFileName))
        {
            path = _storage.GetDownloadFilePath(live.OutputFileName);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The saved file is not available yet, or it has been moved.");
    }

    private void OpenSelected()
    {
        var row = SelectedJob();
        if (row.State != "COMPLETED") throw new InvalidOperationException("Wait for the download to finish before opening it.");
        var path = SelectedPath(row);
        if (Directory.Exists(path)) { OpenFolder(path); return; }
        var safeExtensions = new[] { ".mp4", ".mkv", ".webm", ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".aac", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".zip" };
        if (!safeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) { RevealSelected(); return; }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void RevealSelected()
    {
        var path = SelectedPath(SelectedJob());
        if (Directory.Exists(path)) { OpenFolder(path); return; }
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
    }

    private async Task SaveSelectedAsync()
    {
        var row = SelectedJob();
        if (row.State != "COMPLETED") throw new InvalidOperationException("Wait for the download to finish before saving a copy.");
        var source = SelectedPath(row);
        if (Directory.Exists(source)) source = await Task.Run(() => _storage.CreatePlaylistArchive(row.Snapshot.Id, $"{row.Snapshot.Id}.zip"), _closing.Token);
        using var dialog = new SaveFileDialog { Title = "Save a copy", FileName = Path.GetFileName(source), Filter = "All files|*.*", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) return;
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, _closing.Token);
        _status.Text = "Copy saved to " + dialog.FileName;
    }
}
