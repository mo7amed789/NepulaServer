using System.Drawing;
using System.Windows.Forms;
using NebulaServer.Helpers;
using NebulaServer.Models.ImageEngine;
using NebulaServer.Services.ImageEngine;
using static NebulaServer.Desktop.DesktopTheme;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm
{
    private readonly TextBox _imageUrl = new() { PlaceholderText = "Paste a page or direct image link…", Dock = DockStyle.Top, AccessibleName = "Image source URL" };
    private readonly FlowLayoutPanel _imageGallery = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(12) };
    private readonly HashSet<ImageCandidate> _selectedImages = new();
    private ImageCandidate? _focusedImage;
    private readonly Label _imageMetadata = Label("Select an image to see its details.", 9);
    private readonly PictureBox _imagePreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label _imageStatus = Label("Enter a link to find images. Check the images you want to save.", 9);
    private CancellationTokenSource? _imageOperation;
    private string? _analyzedSource;

    private Control BuildImagesPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var source = Card("Find images");
        source.Controls.Add(_imageUrl);
        var analyze = Button("Find images", () => { }, true);
        var preview = Button("Preview selected", () => { });
        var save = Button("Save selected…", () => { });
        var cancel = Button("Cancel operation", () => _imageOperation?.Cancel());
        async Task RunImages(Func<CancellationToken, Task> action)
        {
            if (_imageOperation != null) return;
            if (!EnsureActivatedForDownload()) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _imageOperation = operation;
            analyze.Enabled = preview.Enabled = save.Enabled = false;
            try { await action(operation.Token); }
            catch (OperationCanceledException) { if (!IsDisposed) _imageStatus.Text = "Operation canceled. Any completed saves were kept."; }
            catch (Exception ex) { _imageStatus.Text = "The operation could not be completed."; ShowError(ex); }
            finally
            {
                _imageOperation = null;
                if (!IsDisposed) analyze.Enabled = preview.Enabled = save.Enabled = true;
            }
        }
        analyze.Click += async (_, _) => await RunImages(async token =>
        {
            var url = DownloadOptions.ValidateUrl(_imageUrl.Text);
            var uri = new Uri(url);
            _imageStatus.Text = "Finding images…";
            await SafeOutboundHttp.EnsureSafeUriAsync(uri, token);
            var result = await _services.GetRequiredService<ImageExtractionEngine>().ExtractAsync(uri, token);
            token.ThrowIfCancellationRequested();
            _analyzedSource = url;
            foreach (Control old in _imageGallery.Controls.Cast<Control>().ToArray()) old.Dispose();
            _selectedImages.Clear(); _focusedImage = null;
            _imagePreview.Image?.Dispose(); _imagePreview.Image = null;
            foreach (var candidate in result.Images)
            {
                var card = new Panel { Width = 170, Height = 156, Margin = new Padding(0, 0, 12, 12), BackColor = Background };
                var thumbnail = new MediaThumbnail(_services.GetRequiredService<IHttpClientFactory>()) { Dock = DockStyle.Fill };
                var select = new CheckBox { Dock = DockStyle.Bottom, Height = 32, Text = candidate.Width.HasValue ? $"{candidate.Width} × {candidate.Height}" : "Original image", AccessibleName = "Select " + candidate.Url };
                select.CheckedChanged += (_, _) =>
                {
                    if (select.Checked) _selectedImages.Add(candidate); else _selectedImages.Remove(candidate);
                    _focusedImage = candidate;
                    _imageMetadata.Text = $"Dimensions: {candidate.Width} × {candidate.Height}\nFormat: {candidate.MimeType ?? "Original"}\n\n{candidate.Url}";
                    _imageStatus.Text = $"{result.Images.Count} images found · {_selectedImages.Count} selected";
                };
                thumbnail.Click += (_, _) => select.Checked = !select.Checked;
                card.Controls.Add(thumbnail); card.Controls.Add(select); _imageGallery.Controls.Add(card);
                _ = thumbnail.LoadAsync(candidate.Url);
            }
            _imageStatus.Text = $"{result.Images.Count} images found · {result.Title}";
        });
        preview.Click += async (_, _) => await RunImages(async token =>
        {
            var candidate = _focusedImage ?? throw new InvalidOperationException("Select an image first.");
            _imageStatus.Text = "Loading preview…";
            var path = await DownloadImageAsync(candidate, RuntimePaths.Temp, token);
            try
            {
                token.ThrowIfCancellationRequested();
                using var image = Image.FromFile(path);
                var old = _imagePreview.Image;
                _imagePreview.Image = new Bitmap(image);
                old?.Dispose();
                _imageStatus.Text = $"Preview · {image.Width} × {image.Height}";
            }
            catch (ArgumentException) { _imageStatus.Text = "Windows cannot preview this format. You can still save the original image."; }
            finally { File.Delete(path); }
        });
        save.Click += async (_, _) => await RunImages(async token =>
        {
            var selected = _selectedImages.ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Select one or more images first.");
            using var dialog = new FolderBrowserDialog { Description = "Choose where to save the selected images", UseDescriptionForTitle = true, InitialDirectory = _storage.DownloadsPath };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var saved = 0;
            foreach (var candidate in selected)
            {
                _imageStatus.Text = $"Saving image {saved + 1} of {selected.Count}…";
                await DownloadImageAsync(candidate, dialog.SelectedPath, token);
                saved++;
            }
            _imageStatus.Text = $"Saved {saved} images to {dialog.SelectedPath}";
        });
        source.Controls.Add(Row(analyze, preview, save, cancel));
        layout.Controls.Add(source);
        layout.Controls.Add(_imageStatus);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16) };
        _imageMetadata.Dock = DockStyle.Bottom; _imageMetadata.AutoSize = false; _imageMetadata.Height = 120;
        previewPanel.Controls.Add(_imagePreview); previewPanel.Controls.Add(_imageMetadata);
        split.Controls.Add(_imageGallery, 0, 0); split.Controls.Add(previewPanel, 1, 0);
        layout.Controls.Add(split);
        layout.Controls.Add(Label("Images are saved at their original resolution. Preview supports formats available in Windows.", 9));
        return layout;
    }

    private async Task<string> DownloadImageAsync(ImageCandidate candidate, string directory, CancellationToken token)
    {
        await SafeOutboundHttp.EnsureSafeUriAsync(new Uri(candidate.Url), token);
        return await _services.GetRequiredService<ImageDownloadManager>().DownloadAndValidateAsync(candidate, directory,
            _analyzedSource is null ? null : new Uri(_analyzedSource), token);
    }
}
