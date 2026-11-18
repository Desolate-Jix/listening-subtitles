using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

/// <summary>A real alpha-composited desktop lyric window; no transparency color key.</summary>
internal sealed class LyricOverlay : Form
{
    private string _original = "", _translation = "", _live = "", _status = "", _previous = "";
    private string _language = "";
    private bool _active, _busy, _ready, _dragging, _lyricsLocked, _toolbarVisible, _refreshing;
    private LyricAction _hotAction, _pressedAction;
    private long _hoverLeaveAt;
    private int _fontSize = 20;
    private float _scale = 1;
    private Bitmap? _frame;
    private LyricRenderer.Frame? _layout;
    private Point _dragMouse, _dragLocation;
    private readonly ToolTip _toolTip = new() { InitialDelay = 650, ReshowDelay = 150, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 80 };
    private string _tip = "";

    internal event Action? ToggleRequested;
    internal event Action<int>? FontSizeRequested;
    internal event Action? LockRequested;
    internal event Action? MenuRequested;
    internal event Action? UserCloseRequested;
    internal event Action<Win32Exception>? DisplayFailed;

    internal LyricOverlay()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        MaximizeBox = false; MinimizeBox = true;
        Icon = SystemIcons.Application;
        Text = AppText.T("听力字幕 · 桌面歌词", "Listening Subtitles · Desktop lyrics");
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _hoverTimer.Tick += (_, _) => PollHover();
        _ready = true;
        ApplySize(true);
        RefreshFrame();
    }

    // Showing the lyrics does not steal focus, start capture, or activate the control panel.
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered;
            if (_lyricsLocked) parameters.ExStyle |= WsExTransparent;
            return parameters;
        }
    }

    internal bool LyricsLocked
    {
        get => _lyricsLocked;
        set
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (InvokeRequired) throw new InvalidOperationException("Update the lyric lock on the UI thread.");
            if (_lyricsLocked == value) return;
            // Change only the input bit. WinForms UpdateStyles or an opacity/color-key
            // API can replace the layered-window state used by UpdateLayeredWindow.
            if (IsHandleCreated) ApplyNativeLock(value);
            _lyricsLocked = value;
            ResetInteraction();
            SetHoverState(false, LyricAction.None);
            UpdateAccessibility();
            UpdateHoverTimer();
        }
    }

    internal void SetContent(string original, string translation, string live, string status, bool active, bool busy, int fontSize, string previous = "")
    {
        if (IsDisposed) return;
        if (InvokeRequired) throw new InvalidOperationException("Update lyrics on the UI thread.");
        original ??= ""; translation ??= ""; live ??= ""; status ??= ""; previous ??= "";
        fontSize = Math.Clamp(fontSize, 12, 36);
        var languageChanged = _language != AppText.Language;
        if (!languageChanged && _original == original && _translation == translation && _live == live && _status == status && _previous == previous &&
            _active == active && _busy == busy && _fontSize == fontSize) return;
        var resize = _fontSize != fontSize;
        _original = original; _translation = translation; _live = live; _status = status; _previous = previous;
        _active = active; _busy = busy; _fontSize = fontSize; _language = AppText.Language;
        Text = AppText.T("听力字幕 · 桌面歌词", "Listening Subtitles · Desktop lyrics");
        AccessibleName = Text;
        UpdateAccessibility();
        if (resize) { ResetInteraction(); ApplySize(false); }
        RefreshFrame();
        UpdateToolTip();
        if (languageChanged) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }

    /// <summary>The exact same premultiplied-alpha pixels supplied to UpdateLayeredWindow.</summary>
    internal Bitmap CaptureFrame()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_frame is null) RefreshFrame();
        return _frame!.Clone(new Rectangle(Point.Empty, _frame.Size), PixelFormat.Format32bppPArgb);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _scale = DeviceDpi / 96f;
        ApplySize(true);
        RefreshFrame();
        UpdateHoverTimer();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _hoverTimer.Stop();
        ResetInteraction();
        _toolbarVisible = false; _hotAction = LyricAction.None;
        base.OnHandleDestroyed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ResetInteraction();
        _scale = e.DeviceDpiNew / 96f;
        ApplySize(false);
        RefreshInteractionFrame();
        UpdateHoverTimer();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_ready || _refreshing) return;
        ResetInteraction();
        UpdateHoverTimer();
        if (ClientSize.Width > 0 && ClientSize.Height > 0 && WindowState != FormWindowState.Minimized) RefreshInteractionFrame();
    }

    protected override void OnShown(EventArgs e)
    {
        RefreshFrame();
        base.OnShown(e);
        UpdateHoverTimer();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!_ready) return;
        UpdateHoverTimer();
        // OnShown fires only once. Re-present the hidden-state frame on later shows
        // so Windows cannot restore a stale surface with its toolbar still visible.
        if (Visible && WindowState != FormWindowState.Minimized) RefreshInteractionFrame();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void ApplySize(bool initial)
    {
        if (!_ready) return;
        var area = initial ? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720) : Screen.FromRectangle(Bounds).WorkingArea;
        var width = Math.Min((int)Math.Ceiling(1120 * _scale), Math.Max(240, area.Width - (int)Math.Ceiling(40 * _scale)));
        var pixels = Math.Clamp(_fontSize * 1.55f, 20, 56);
        var height = Math.Min((int)Math.Ceiling((pixels * 4.8f + 170) * _scale), Math.Max(160, area.Height - (int)(24 * _scale)));
        _refreshing = true;
        try
        {
            var center = Left + Width / 2;
            var bottom = Bottom;
            Size = new(width, height);
            Location = initial
                ? new(area.Left + (area.Width - Width) / 2, Math.Max(area.Top, area.Bottom - Height - (int)Math.Ceiling(48 * _scale)))
                : new(center - Width / 2, bottom - Height);
            Location = BoundLocation(Location, area);
        }
        finally { _refreshing = false; }
    }

    private void RefreshFrame()
    {
        if (!_ready || _refreshing || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        _refreshing = true;
        try
        {
            var rendered = LyricRenderer.Render(ClientSize, _scale, _original, _translation, _live, _status, _active, _busy, _fontSize,
                _previous, _toolbarVisible && !_lyricsLocked, _hotAction);
            var old = _frame;
            _layout = rendered; _frame = rendered.Image;
            old?.Dispose();
            if (IsHandleCreated && Visible && WindowState != FormWindowState.Minimized) PresentFrame();
        }
        finally { _refreshing = false; }
    }

    private void RefreshInteractionFrame()
    {
        try { RefreshFrame(); }
        catch (Win32Exception error)
        {
            if (DisplayFailed is { } handleFailure) handleFailure(error);
            else throw;
        }
    }

    private void PresentFrame()
    {
        if (_frame is null) return;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw DisplayError();
        var memoryDc = IntPtr.Zero; var nativeBitmap = IntPtr.Zero; var previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) throw DisplayError();
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = _frame.Width, Height = -_frame.Height,
                    Planes = 1, BitCount = 32, Compression = 0
                }
            };
            nativeBitmap = CreateDIBSection(memoryDc, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (nativeBitmap == IntPtr.Zero || pixels == IntPtr.Zero) throw DisplayError();
            previous = SelectObject(memoryDc, nativeBitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw DisplayError();
            var data = _frame.LockBits(new Rectangle(Point.Empty, _frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[_frame.Width * 4];
                for (var y = 0; y < _frame.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(pixels, y * row.Length), row.Length);
                }
            }
            finally { _frame.UnlockBits(data); }
            var destination = new NativePoint(Left, Top);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(_frame.Width, _frame.Height);
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2)) throw DisplayError();
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1) && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (nativeBitmap != IntPtr.Zero) DeleteObject(nativeBitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Win32Exception DisplayError() => new(Marshal.GetLastWin32Error(), AppText.T("无法显示透明字幕层，请使用完整控制窗口。", "The transparent lyric layer could not be displayed. Use the full control window."));

    private bool CanInteract => _ready && !IsDisposed && !Disposing && IsHandleCreated && Visible &&
        WindowState != FormWindowState.Minimized && !_lyricsLocked;

    private void UpdateHoverTimer()
    {
        if (CanInteract)
        {
            _hoverTimer.Start();
            PollHover();
        }
        else
        {
            _hoverTimer.Stop();
            ResetInteraction();
            SetHoverState(false, LyricAction.None);
        }
    }

    private void PollHover()
    {
        if (CanInteract) UpdateHover(PointToClient(Cursor.Position));
    }

    // Polling also sees fully transparent spaces, where the layered HWND receives
    // no mouse messages. HoverArea joins the lyric rows, toolbar and intervening gap.
    private void UpdateHover(Point point)
    {
        if (!CanInteract || _layout is null) return;
        var inside = _layout.HoverArea.Contains(point);
        var visible = inside || _dragging || _pressedAction != LyricAction.None;
        if (visible) _hoverLeaveAt = 0;
        else if (_toolbarVisible)
        {
            if (_hoverLeaveAt == 0) _hoverLeaveAt = Environment.TickCount64;
            visible = Environment.TickCount64 - _hoverLeaveAt < 240;
        }
        var hot = LyricAction.None;
        if (visible && inside)
        {
            // Bounds remain stable while hidden, so the first hovered button can be
            // highlighted in the same render that reveals the toolbar.
            for (var action = LyricAction.Toggle; action <= LyricAction.Menu; action++)
                if (_layout.BoundsFor(action).Contains(point)) { hot = action; break; }
        }
        SetHoverState(visible, hot);
        Cursor = hot != LyricAction.None ? Cursors.Hand : inside || _dragging ? Cursors.SizeAll : Cursors.Default;
    }

    private void SetHoverState(bool visible, LyricAction hot)
    {
        if (!visible) hot = LyricAction.None;
        if (_toolbarVisible != visible || _hotAction != hot)
        {
            _toolbarVisible = visible; _hotAction = hot;
            RefreshInteractionFrame();
        }
        if (!IsDisposed) UpdateToolTip();
    }

    private void UpdateToolTip()
    {
        var tip = !_toolbarVisible || _lyricsLocked ? "" : _hotAction switch
        {
            LyricAction.Toggle => _busy ? AppText.T("正在处理，请稍候", "Working, please wait")
                : _active ? AppText.T("停止转写（空格）", "Stop transcription (Space)") : AppText.T("开始转写（空格）", "Start transcription (Space)"),
            LyricAction.Smaller => AppText.T("减小字号", "Decrease font size"),
            LyricAction.Larger => AppText.T("增大字号", "Increase font size"),
            LyricAction.Lock => AppText.T("锁定字幕并穿透鼠标；从托盘菜单解锁", "Lock lyrics and pass mouse input through; unlock from the tray menu"),
            LyricAction.Menu => AppText.T("设置、历史与显示选项（Shift+F10）", "Settings, history and display options (Shift+F10)"),
            _ => AppText.T("拖动文字移动字幕", "Drag the words to move the lyrics")
        };
        if (_tip == tip) return;
        _tip = tip;
        _toolTip.SetToolTip(this, tip);
    }

    private void UpdateAccessibility() => AccessibleDescription = _lyricsLocked
        ? AppText.T("字幕已锁定，鼠标完全穿透；从托盘菜单解锁。", "Lyrics are locked and mouse input passes through. Unlock from the tray menu.")
        : AppText.T("悬停显示控制条。空格或回车开始、停止；Shift+F10 打开菜单。拖动文字移动。", "Hover to show controls. Space or Enter starts and stops; Shift+F10 opens the menu. Drag the words to move.");

    private void ResetInteraction()
    {
        _dragging = false; _pressedAction = LyricAction.None; _hoverLeaveAt = 0;
        if (IsHandleCreated && Capture) Capture = false;
        Cursor = Cursors.Default;
    }

    private void ApplyNativeLock(bool locked)
    {
        Marshal.SetLastPInvokeError(0);
        var style = IntPtr.Size == 8 ? GetWindowLongPtr(Handle, GwlExStyle).ToInt64() : GetWindowLong(Handle, GwlExStyle);
        var error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0) throw LockError(error);
        var updated = locked ? style | WsExTransparent : style & ~WsExTransparent;
        if (updated == style) return;
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(updated)).ToInt64()
            : SetWindowLong(Handle, GwlExStyle, unchecked((int)updated));
        error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw LockError(error);
    }

    private static Win32Exception LockError(int error) => new(error, AppText.T("无法更改字幕锁定状态，请从托盘打开完整控制窗口。", "The lyric lock could not be changed. Open the full control window from the tray."));

    protected override void WndProc(ref Message message)
    {
        const int hitTest = 0x0084, contextMenu = 0x007B, close = 0x0010;
        if (message.Msg == close && !Disposing && UserCloseRequested is { } requestClose)
        {
            requestClose();
            message.Result = IntPtr.Zero;
            return;
        }
        if (message.Msg == hitTest && _frame is not null)
        {
            if (_lyricsLocked) { message.Result = new IntPtr(-1); return; }
            var position = unchecked((long)message.LParam);
            var point = PointToClient(new Point(unchecked((short)position), unchecked((short)(position >> 16))));
            // Zero-alpha pixels pass through Windows' layered-window hit testing. Explicitly
            // exclude near-invisible shadow pixels as well, leaving the desktop usable.
            var alpha = point.X >= 0 && point.Y >= 0 && point.X < _frame.Width && point.Y < _frame.Height ? _frame.GetPixel(point.X, point.Y).A : 0;
            message.Result = alpha > 8 ? new IntPtr(1) : new IntPtr(-1); // HTCLIENT / HTTRANSPARENT
            return;
        }
        if (message.Msg == contextMenu)
        {
            if (CanInteract) MenuRequested?.Invoke();
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanInteract || _layout is null || !_layout.HoverArea.Contains(e.Location)) return;
        Activate();
        _pressedAction = _toolbarVisible ? _layout.HitTest(e.Location) : LyricAction.None;
        if (_pressedAction == LyricAction.None)
        {
            _dragging = true; _dragMouse = Cursor.Position; _dragLocation = Location;
            Cursor = Cursors.SizeAll;
        }
        Capture = true;
        UpdateHover(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!CanInteract) return;
        if (_dragging)
        {
            var cursor = Cursor.Position;
            var area = Screen.FromPoint(cursor).WorkingArea;
            Location = BoundLocation(new Point(_dragLocation.X + cursor.X - _dragMouse.X, _dragLocation.Y + cursor.Y - _dragMouse.Y), area);
            return;
        }
        UpdateHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var action = CanInteract && _toolbarVisible && _layout?.HitTest(e.Location) == _pressedAction ? _pressedAction : LyricAction.None;
        _dragging = false; _pressedAction = LyricAction.None;
        Capture = false;
        switch (action)
        {
            case LyricAction.Toggle: if (!_busy) ToggleRequested?.Invoke(); break;
            case LyricAction.Smaller: FontSizeRequested?.Invoke(-2); break;
            case LyricAction.Larger: FontSizeRequested?.Invoke(2); break;
            case LyricAction.Lock: LockRequested?.Invoke(); break;
            case LyricAction.Menu: MenuRequested?.Invoke(); break;
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) { _dragging = false; _pressedAction = LyricAction.None; }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        PollHover();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData is Keys.Space or Keys.Enter)
        {
            if (CanInteract && !_busy) ToggleRequested?.Invoke();
            return true;
        }
        if (keyData == Keys.Apps || keyData == (Keys.Shift | Keys.F10)) { if (CanInteract) MenuRequested?.Invoke(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing && !Disposing)
        {
            e.Cancel = true;
            UserCloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    private Point BoundLocation(Point candidate, Rectangle area) => new(
        Math.Clamp(candidate.X, area.Left, Math.Max(area.Left, area.Right - Width)),
        Math.Clamp(candidate.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ready = false; _hoverTimer.Stop(); _hoverTimer.Dispose();
            ResetInteraction(); _toolTip.Dispose(); _frame?.Dispose(); _frame = null; _layout = null;
        }
        base.Dispose(disposing);
    }

    private const int GwlExStyle = -20, WsExLayered = 0x00080000, WsExTransparent = 0x00000020;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { internal int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize(int width, int height) { internal int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { internal byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter; internal uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Colors; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size,
        IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
