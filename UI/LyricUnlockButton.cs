using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

/// <summary>The sole input target offered over otherwise click-through locked lyrics.</summary>
internal sealed class LyricUnlockButton : Form
{
    private float _scale;
    private string _label = "";
    private Font? _labelFont;
    private bool _pressed, _hot;

    internal event Action? UnlockRequested;
    internal bool IsPressed => _pressed;

    internal LyricUnlockButton()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ControlBox = MaximizeBox = MinimizeBox = false;
        TabStop = false;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(27, 34, 43);
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        Configure(1);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    internal void Configure(float scale)
    {
        var label = AppText.T("解锁歌词", "Unlock lyrics");
        if (_scale == scale && _label == label) return;
        _scale = scale; _label = label;
        var previousFont = _labelFont;
        _labelFont = new Font("Microsoft YaHei UI", 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        previousFont?.Dispose();
        using var probe = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(probe);
        var width = (int)Math.Ceiling(graphics.MeasureString(label, _labelFont).Width + 50 * scale);
        Size = new(width, (int)Math.Ceiling(34 * scale));
        using var path = ButtonPath();
        var previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
        Text = AccessibleName = label;
        AccessibleDescription = AppText.T("点击解锁歌词；其他字幕区域保持鼠标穿透。也可从托盘菜单解锁。", "Click to unlock lyrics. Other lyric areas pass mouse input through. The tray menu can also unlock them.");
        Invalidate();
    }

    internal Bitmap CaptureFrame()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        if (Region is not null) graphics.SetClip(Region, CombineMode.Intersect);
        DrawButton(graphics);
        return bitmap;
    }

    private GraphicsPath ButtonPath()
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Height - 1, (int)Math.Ceiling(18 * _scale));
        var right = Width - 1; var bottom = Height - 1;
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(right - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) => DrawButton(e.Graphics);

    private void DrawButton(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ButtonPath();
        using var fill = new SolidBrush(_pressed && _hot ? Color.FromArgb(47, 65, 76) : _hot ? Color.FromArgb(39, 50, 63) : BackColor);
        using var border = new Pen(Color.FromArgb(134, 159, 177), Math.Max(1, _scale));
        graphics.FillPath(fill, path); graphics.DrawPath(border, path);
        var centerY = Height / 2f;
        using var icon = new Pen(Color.FromArgb(183, 237, 216), 1.6f * _scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(icon, 13 * _scale, centerY - 8 * _scale, 9 * _scale, 11 * _scale, 170, 215);
        graphics.DrawRectangle(icon, 11 * _scale, centerY - _scale, 13 * _scale, 10 * _scale);
        graphics.DrawLine(icon, 17.5f * _scale, centerY + 3 * _scale, 17.5f * _scale, centerY + 5 * _scale);
        if (_labelFont is null) return;
        using var foreground = new SolidBrush(Color.FromArgb(241, 246, 250));
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(_label, _labelFont, foreground, new RectangleF(33 * _scale, 0, Width - 41 * _scale, Height), format);
    }

    private bool ContainsButton(Point point) => ClientRectangle.Contains(point) && (Region?.IsVisible(point) ?? false);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Visible || !Enabled || !ContainsButton(e.Location)) return;
        _pressed = _hot = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = ContainsButton(e.Location);
        if (_hot != hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var unlock = _pressed && Visible && Enabled && ContainsButton(e.Location);
        _pressed = false;
        Capture = false;
        Invalidate();
        if (unlock) UnlockRequested?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot) { _hot = false; Invalidate(); }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _pressed) { _pressed = false; Invalidate(); }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) { _pressed = _hot = false; if (Capture) Capture = false; }
        base.OnVisibleChanged(e);
    }

    protected override void OnResize(EventArgs e)
    {
        _pressed = false;
        if (IsHandleCreated && Capture) Capture = false;
        base.OnResize(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message.Msg == 0x0010) { Hide(); message.Result = IntPtr.Zero; return; } // No independent app lifetime.
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _labelFont?.Dispose(); _labelFont = null; }
        base.Dispose(disposing);
    }
}
