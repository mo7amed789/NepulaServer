using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NebulaServer.Desktop;

internal static class EnterpriseFonts
{
    private static readonly PrivateFontCollection Fonts = new();
    private static bool _loaded;
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string path, uint flags, IntPtr reserved);
    public static Font Create(float size, bool bold = false, bool mono = false)
    {
        if (!_loaded)
        {
            _loaded = true;
            var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
            if (Directory.Exists(directory)) foreach (var file in Directory.GetFiles(directory, "*.ttf"))
            { Fonts.AddFontFile(file); AddFontResourceEx(file, 0x10, IntPtr.Zero); }
        }
        return new Font(mono ? "IBM Plex Mono" : "IBM Plex Sans", size, bold ? FontStyle.Bold : FontStyle.Regular);
    }
}

internal sealed class EnterpriseCard : TableLayoutPanel
{
    public EnterpriseCard() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        using var pen = new Pen(DesktopTheme.Border);
        e.Graphics.DrawPath(pen, path);
    }
    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) return path;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

// WinForms lets Windows paint disabled Button text, which can become nearly
// black under the user's system dark-mode settings. Paint the small set of
// app buttons ourselves so their contrast is controlled by DesktopTheme.
internal sealed class EnterpriseButton : Button
{
    public EnterpriseButton() { DoubleBuffered = true; UseVisualStyleBackColor = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var path = EnterpriseCard.Rounded(bounds, 10);
        using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, path);
        if (FlatAppearance.BorderSize > 0)
        {
            using var pen = new Pen(FlatAppearance.BorderColor.IsEmpty ? DesktopTheme.Border : FlatAppearance.BorderColor);
            e.Graphics.DrawPath(pen, path);
        }

        if (Tag is "nav-active")
        {
            using var active = new SolidBrush(DesktopTheme.Accent);
            using var activePath = EnterpriseCard.Rounded(new Rectangle(0, 8, 3, Math.Max(1, Height - 16)), 2);
            e.Graphics.FillPath(active, activePath);
        }

        var textColor = Enabled ? ForeColor : DesktopTheme.Muted;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3));
    }
}

/// <summary>Owner-drawn combo box so Windows light/dark settings cannot make
/// the popup unreadable or replace the app's visual language.</summary>
internal sealed class EnterpriseComboBox : ComboBox
{
    public EnterpriseComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 28;
        DropDownHeight = 320;
        IntegralHeight = false;
        FlatStyle = FlatStyle.Flat;
        BackColor = DesktopTheme.Surface;
        ForeColor = DesktopTheme.Ink;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            e.DrawBackground();
            return;
        }

        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var background = selected ? DesktopTheme.Accent : DesktopTheme.Surface;
        var foreground = selected && DesktopTheme.Background == Color.Black ? Color.FromArgb(24, 24, 18) : selected ? Color.White : DesktopTheme.Ink;
        using (var brush = new SolidBrush(background))
            e.Graphics.FillRectangle(brush, e.Bounds);

        var bounds = Rectangle.Inflate(e.Bounds, -10, 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, bounds,
            foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (!selected && e.Index < Items.Count - 1)
        {
            using var pen = new Pen(DesktopTheme.Border);
            e.Graphics.DrawLine(pen, e.Bounds.Left + 10, e.Bounds.Bottom - 1, e.Bounds.Right - 10, e.Bounds.Bottom - 1);
        }
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch() { AutoSize = false; Width = 300; Height = 40; DoubleBuffered = true; Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(Width - 44, (Height - 22) / 2, 40, 22);
        using var path = EnterpriseCard.Rounded(track, 11);
        var offTrack = DesktopTheme.Background == Color.Black ? Color.FromArgb(52, 52, 52) : Color.FromArgb(205, 213, 223);
        using var brush = new SolidBrush(Enabled && Checked ? DesktopTheme.Accent : offTrack);
        e.Graphics.FillPath(brush, path);
        e.Graphics.FillEllipse(Brushes.White, track.X + (Checked ? 20 : 2), track.Y + 2, 18, 18);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Math.Max(1, Width - 55), Height), Enabled ? ForeColor : DesktopTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}
