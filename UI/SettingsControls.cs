using System.Drawing.Drawing2D;

namespace ListeningSubtitles.UI;

internal static class SettingsPalette
{
    public static readonly Color Canvas = Color.FromArgb(247, 248, 244);
    public static readonly Color Sidebar = Color.FromArgb(237, 241, 235);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(30, 47, 43);
    public static readonly Color Muted = Color.FromArgb(88, 106, 98);
    public static readonly Color Accent = Color.FromArgb(25, 91, 76);
    public static readonly Color Selected = Color.FromArgb(218, 235, 225);
    public static readonly Color Border = Color.FromArgb(207, 218, 209);
    public static readonly Color Error = Color.FromArgb(156, 47, 39);
}

// The native list keeps arrow-key selection, focus cues and screen-reader names.
internal sealed class SettingsNavigation : ListBox
{
    public SettingsNavigation()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None;
        BackColor = SettingsPalette.Sidebar; ForeColor = SettingsPalette.Ink;
        IntegralHeight = false; ItemHeight = 52;
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e); ItemHeight = (int)Math.Round(52 * DeviceDpi / 96f);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        using (var background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
        var selected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds; var scale = DeviceDpi / 96f;
        if (selected)
        {
            var box = Rectangle.Inflate(bounds, -(int)(4 * scale), -(int)(4 * scale));
            using var background = new SolidBrush(SettingsPalette.Selected);
            using var path = RoundedRectangle(box, (int)(8 * scale));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(background, path);
            using var indicator = new SolidBrush(SettingsPalette.Accent);
            e.Graphics.FillRectangle(indicator, box.Left, box.Top + (int)(14 * scale), (int)(3 * scale), box.Height - (int)(28 * scale));
        }
        var item = (SettingsNavigationItem)Items[e.Index];
        DrawCategoryIcon(e.Graphics, e.Index, bounds.Left + (int)(16 * scale), bounds.Top + (int)(16 * scale), scale, selected);
        var textBounds = new Rectangle(bounds.Left + (int)(48 * scale), bounds.Top + (int)(15 * scale), bounds.Width - (int)(56 * scale), (int)(26 * scale));
        TextRenderer.DrawText(e.Graphics, item.Title, Font, textBounds, selected ? SettingsPalette.Accent : SettingsPalette.Ink,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if ((e.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -(int)(6 * scale), -(int)(6 * scale)), SettingsPalette.Accent, BackColor);
    }
    private static void DrawCategoryIcon(Graphics graphics, int index, int x, int y, float scale, bool selected)
    {
        var previous = graphics.Save(); graphics.TranslateTransform(x, y); graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(selected ? SettingsPalette.Accent : SettingsPalette.Muted, 1.7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (index == 0)
        {
            graphics.DrawRectangle(pen, 1, 1, 16, 12); graphics.DrawLines(pen, new Point[] { new(4, 13), new(4, 17), new(9, 13) });
            graphics.DrawLine(pen, 5, 5, 13, 5); graphics.DrawLine(pen, 5, 9, 10, 9);
        }
        else if (index == 1)
        {
            graphics.DrawPolygon(pen, new Point[] { new(1, 7), new(5, 7), new(10, 2), new(10, 18), new(5, 13), new(1, 13) });
            graphics.DrawArc(pen, 8, 4, 8, 12, -65, 130); graphics.DrawArc(pen, 8, 1, 13, 18, -55, 110);
        }
        else
        {
            graphics.DrawEllipse(pen, 1, 2, 8, 8); graphics.DrawLine(pen, 8, 9, 18, 19);
            graphics.DrawLine(pen, 13, 14, 16, 11); graphics.DrawLine(pen, 16, 17, 19, 14);
        }
        graphics.Restore(previous);
    }
    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath(); var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
}

internal sealed record SettingsNavigationItem(string Title)
{
    public override string ToString() => Title;
}

internal sealed class SettingsCard : TableLayoutPanel
{
    public SettingsCard()
    {
        BackColor = SettingsPalette.Surface; ColumnCount = 1; AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
        Padding = new(20, 16, 20, 18); Margin = new(0, 0, 0, 16);
        ColumnStyles.Add(new(SizeType.Percent, 100));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var border = new Pen(SettingsPalette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

// Padding leaves a comfortable hit area while preserving native input behavior.
internal sealed class SettingsTextFrame : Panel
{
    public SettingsTextFrame(TextBox input)
    {
        BackColor = SettingsPalette.Surface; Height = 38; Padding = new(11, 8, 11, 7); Dock = DockStyle.Top;
        input.BorderStyle = BorderStyle.None; input.Dock = DockStyle.Fill; Controls.Add(input);
        input.Enter += (_, _) => Invalidate(); input.Leave += (_, _) => Invalidate();
        input.EnabledChanged += (_, _) => { BackColor = input.Enabled ? SettingsPalette.Surface : SettingsPalette.Canvas; Invalidate(); };
        Click += (_, _) => input.Focus();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(ContainsFocus ? SettingsPalette.Accent : SettingsPalette.Border, ContainsFocus ? 2 : 1);
        e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
    }
}
