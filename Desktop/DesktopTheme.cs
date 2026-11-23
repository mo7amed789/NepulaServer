using System.Drawing;
using System.Windows.Forms;

namespace NebulaServer.Desktop;

internal static class DesktopTheme
{
    // TOP Game esports theme: warm neutrals, action orange, no red.
    public static Color Background { get; private set; } = Color.FromArgb(247, 244, 242);
    public static Color Surface { get; private set; } = Color.White;
    public static Color Ink { get; private set; } = Color.FromArgb(26, 20, 17);
    public static Color Muted { get; private set; } = Color.FromArgb(125, 116, 108);
    public static Color Accent { get; private set; } = Color.FromArgb(231, 85, 3);
    public static Color Sidebar { get; private set; } = Color.FromArgb(24, 19, 16);
    public static Color Border { get; private set; } = Color.FromArgb(232, 222, 214);

    public static void Apply(bool dark)
    {
        // Dark mode is intentionally true black rather than blue-gray. Keep
        // surfaces just above black so cards remain distinguishable without
        // allowing Windows' theme colors to leak into the app.
        Background = dark ? Color.Black : Color.FromArgb(247, 244, 242);
        // Keep control BackColor values opaque: WinForms rejects alpha colors
        // on standard TextBox/ComboBox/Panel controls. The actual glass layer
        // is provided by the Windows DWM backdrop and custom rounded painting.
        Surface = dark ? Color.FromArgb(21, 18, 16) : Color.White;
        Ink = dark ? Color.FromArgb(247, 244, 242) : Color.FromArgb(26, 20, 17);
        Muted = dark ? Color.FromArgb(184, 174, 166) : Color.FromArgb(125, 116, 108);
        // TOP Game action orange (#E75503): the single action color of the
        // design language, paired with a near-black label for contrast.
        Accent = Color.FromArgb(231, 85, 3);
        Sidebar = dark ? Color.FromArgb(11, 9, 8) : Color.FromArgb(24, 19, 16);
        Border = dark ? Color.FromArgb(58, 48, 40) : Color.FromArgb(232, 222, 214);
    }

    public static Label Label(string text, float size = 10, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = Ink,
        Font = EnterpriseFonts.Create(size, bold),
        Margin = new Padding(0, 0, 0, 8)
    };

    public static Button Button(string text, Action click, bool primary = false)
    {
        var button = new EnterpriseButton
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(104, 38), Padding = new Padding(12, 5, 12, 5),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Color.FromArgb(26, 14, 6) : Ink, Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 10, 0), UseVisualStyleBackColor = false,
            Tag = primary ? "primary" : null
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 99, 94) : Background;
        button.Click += (_, _) => click();
        return button;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 0, 0, 12) };
        row.Controls.AddRange(controls);
        return row;
    }

    public static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Padding = new Padding(0), Margin = new Padding(0) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    public static TableLayoutPanel Card(string title)
    {
        var card = new EnterpriseCard { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 0, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        card.BackColor = Surface;
        card.Padding = new Padding(20);
        card.Margin = new Padding(0, 0, 0, 18);
        card.ControlAdded += (_, _) =>
        {
            while (card.RowStyles.Count < card.Controls.Count)
                card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        };
        card.Controls.Add(Label(title, 13, true));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.SizeChanged += (_, _) =>
        {
            var width = Math.Max(80, card.ClientSize.Width - card.Padding.Horizontal);
            foreach (Control child in card.Controls)
                if (child is System.Windows.Forms.Label or FlowLayoutPanel) child.MaximumSize = new Size(width, 0);
        };
        return card;
    }

    public static Control Field(string label, Control input, int width = 180)
    {
        var field = Stack();
        field.Dock = DockStyle.None;
        field.Width = width;
        field.Margin = new Padding(0, 0, 16, 10);
        field.Controls.Add(Label(label, 9, true));
        input.Width = width;
        input.Dock = DockStyle.Top;
        input.AccessibleName = label;
        field.Controls.Add(input);
        return field;
    }

    public static ComboBox Choices(params string[] values)
    {
        var box = new EnterpriseComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(values);
        box.SelectedIndex = 0;
        return box;
    }

    public static DataGridView Grid(params (string Name, string Header, int Weight)[] columns)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Surface, BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
            MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false, ColumnHeadersHeight = 40,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Border, AutoGenerateColumns = false
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        { BackColor = Background, ForeColor = Muted, Font = EnterpriseFonts.Create(9, true), Padding = new Padding(6) };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        { ForeColor = Ink, SelectionBackColor = Color.FromArgb(221, 241, 238), SelectionForeColor = Ink, Padding = new Padding(6) };
        grid.RowTemplate.Height = 42;
        foreach (var (name, header, weight) in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        return grid;
    }
}
