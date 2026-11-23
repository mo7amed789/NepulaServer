using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NebulaServer.Helpers;

namespace NebulaServer.Desktop;

internal sealed class MediaThumbnail : Control
{
    private static readonly SemaphoreSlim Slots = new(4);
    private readonly IHttpClientFactory _clients;
    private CancellationTokenSource? _request;
    private Bitmap? _image;
    private string? _url;
    private string _message = "Media preview";

    public MediaThumbnail(IHttpClientFactory clients)
    {
        _clients = clients;
        DoubleBuffered = true;
        BackColor = DesktopTheme.Background;
        ForeColor = DesktopTheme.Muted;
        AccessibleName = "Media thumbnail";
        Size = new Size(256, 144);
        Margin = new Padding(0, 0, 16, 12);
    }

    public async Task LoadAsync(string? url, bool force = false)
    {
        // A failed request must be retryable. Previously `_url == url` caused
        // a transient CDN/timeout failure to become permanent for that item.
        if ((!force && _url == url && (_image != null || _request != null)) || IsDisposed) return;
        _url = url;
        _request?.Cancel();
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _request = request;
        _image?.Dispose(); _image = null;
        _message = string.IsNullOrWhiteSpace(url) ? "No thumbnail available" : "Loading thumbnail…";
        Invalidate();
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var image = await Task.Run(async () =>
            {
                await Slots.WaitAsync(request.Token);
                try
                {
                using var client = _clients.CreateClient("ImageDownloaderClient");
                Exception? last = null;
                foreach (var candidate in ThumbnailCandidates(url))
                {
                    try
                    {
                        var uri = new Uri(candidate);
                        await SafeOutboundHttp.EnsureSafeUriAsync(uri, request.Token);
                        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, request.Token);
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > 10 * 1024 * 1024)
                            throw new InvalidDataException("Thumbnail too large.");
                        await using var input = await response.Content.ReadAsStreamAsync(request.Token);
                        using var bytes = new MemoryStream();
                        var buffer = new byte[16384];
                        int read;
                        while ((read = await input.ReadAsync(buffer, request.Token)) > 0)
                        {
                            if (bytes.Length + read > 10 * 1024 * 1024) throw new InvalidDataException("Thumbnail too large.");
                            bytes.Write(buffer, 0, read);
                        }
                        bytes.Position = 0;
                        using var original = Image.FromStream(bytes, false, true);
                        if ((long)original.Width * original.Height > 32_000_000) throw new InvalidDataException("Thumbnail dimensions too large.");
                        var scale = Math.Min(1d, 640d / Math.Max(original.Width, original.Height));
                        return new Bitmap(original, new Size(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale))));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { last = ex; }
                }
                throw last ?? new InvalidOperationException("Thumbnail download failed.");
                }
                finally { Slots.Release(); }
            }, request.Token);
            if (IsDisposed || request.IsCancellationRequested || _request != request) { image.Dispose(); return; }
            _image = image;
            _message = "";
            AccessibleDescription = "Media thumbnail loaded";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_request == request && !IsDisposed)
            {
                _message = "Thumbnail unavailable";
                AccessibleDescription = _message;
            }
        }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
            if (!IsDisposed) Invalidate();
        }
    }

    private static IEnumerable<string> ThumbnailCandidates(string url)
    {
        yield return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;
        if (!uri.Host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase)) yield break;
        var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"/vi/([^/]+)/[^/]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) yield break;
        var id = match.Groups[1].Value;
        yield return $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        yield return $"https://i.ytimg.com/vi/{id}/sddefault.jpg";
        yield return $"https://i.ytimg.com/vi/{id}/default.jpg";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_image is null)
        {
            TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        // Fill the preview area without distorting the artwork. The centered
        // crop keeps the thumbnail edge-to-edge instead of leaving letterbox
        // bands inside the card.
        var scale = Math.Max((double)Width / _image.Width, (double)Height / _image.Height);
        var size = new Size((int)Math.Ceiling(_image.Width * scale), (int)Math.Ceiling(_image.Height * scale));
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_image, new Rectangle((Width - size.Width) / 2, (Height - size.Height) / 2, size.Width, size.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _request?.Cancel(); _image?.Dispose(); _image = null; }
        base.Dispose(disposing);
    }
}
