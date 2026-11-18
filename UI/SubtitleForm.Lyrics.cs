using System.ComponentModel;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed partial class SubtitleForm
{
    private readonly LyricOverlay _overlay = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly Button _lyricsButton;
    private string _overlayStatus = "";
    private Form ActiveSurface => _overlay.Visible ? _overlay : this;

    protected override void OnShown(EventArgs e)
    {
        ShowLyrics();
        base.OnShown(e);
    }

    private bool CanUseMenu() => !_closing && Enabled && _overlay.Enabled &&
        !Application.OpenForms.Cast<Form>().Any(form => form.Modal);

    private void BuildLyricsMenu()
    {
        void Add(string name, EventHandler click) => _menu.Items.Add("", null, click).Name = name;
        _menu.Items.Add(new ToolStripSeparator());
        Add("settings", async (_, _) => await ConfigureAsync());
        Add("history", (_, _) => OpenHistory());
        Add("controlPanel", (_, _) => ShowControlPanel());
        _menu.Items.Add(new ToolStripSeparator());
        Add("larger", (_, _) => ChangeFont(2));
        Add("smaller", (_, _) => ChangeFont(-2));
        Add("translation", (_, _) => _show.Checked = !_show.Checked);
        Add("topmost", (_, _) => _pin.Checked = !_pin.Checked);
        Add("lockLyrics", (_, _) => ToggleLyricsLock());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Opening += (_, e) =>
        {
            if (!CanUseMenu()) { e.Cancel = true; return; }
            _menu.Items["start"]!.Enabled = !_busy && !_session.Active;
            _trayStop.Enabled = _session.Active;
            _menu.Items["settings"]!.Enabled = !_busy;
            ((ToolStripMenuItem)_menu.Items["translation"]!).Checked = _show.Checked;
            ((ToolStripMenuItem)_menu.Items["topmost"]!).Checked = _pin.Checked;
        };
        _overlay.FontSizeRequested += delta => { if (CanUseMenu()) ChangeFont(delta); };
        _overlay.LockRequested += () => { if (CanUseMenu()) ToggleLyricsLock(); };
        _overlay.ToggleRequested += async () =>
        {
            if (_busy || !CanUseMenu()) return;
            if (_session.Active) await StopAsync(); else await StartAsync();
        };
        _overlay.MenuRequested += () =>
        {
            if (!CanUseMenu()) return;
            var point = Cursor.Position;
            if (!_overlay.Bounds.Contains(point))
                point = _overlay.PointToScreen(new Point(_overlay.Width / 2, _overlay.Height / 2));
            _menu.Show(_overlay, _overlay.PointToClient(point));
        };
        _overlay.UserCloseRequested += () => { if (CanUseMenu()) BeginInvoke((Action)Close); };
        _overlay.DisplayFailed += _ => Post(() => { if (_overlay.Visible) HandleOverlayFailure(); });
    }

    private void LocalizeLyricsMenu()
    {
        _menu.Items["start"]!.Text = AppText.T("开始转写", "Start transcription");
        _menu.Items["settings"]!.Text = _configure.Text;
        _menu.Items["history"]!.Text = _historyButton.Text;
        _menu.Items["controlPanel"]!.Text = AppText.T("完整控制窗口", "Full control window");
        _menu.Items["larger"]!.Text = AppText.T("放大字幕", "Larger subtitles");
        _menu.Items["smaller"]!.Text = AppText.T("缩小字幕", "Smaller subtitles");
        _menu.Items["translation"]!.Text = _show.Text;
        _menu.Items["topmost"]!.Text = _pin.Text;
        UpdateLyricsLockLabel();
    }

    private void UpdateLyricsLockLabel() => _menu.Items["lockLyrics"]!.Text = _settings.LyricsLocked
        ? AppText.T("解锁歌词", "Unlock lyrics") : AppText.T("锁定歌词（鼠标穿透）", "Lock lyrics (click-through)");

    private void ToggleLyricsLock()
    {
        var locked = !_overlay.LyricsLocked;
        try { _overlay.LyricsLocked = locked; }
        catch (Win32Exception)
        {
            ShowControlPanel();
            SetState("歌词锁定状态更改失败，可从托盘重试。", "The lyric lock could not be changed. Try again from the tray.");
            return;
        }
        _settings = _settings with
        {
            LyricsLocked = locked,
            LockedLyricCenter = locked ? new Point(_overlay.Left + _overlay.Width / 2, _overlay.Top + _overlay.Height / 2) : null
        };
        UpdateLyricsLockLabel();
        RefreshLyrics();
        SavePreferences();
    }

    private void RestoreLockedPosition(Point center)
    {
        var area = Screen.FromPoint(center).WorkingArea;
        var x = Math.Clamp(center.X, area.Left, area.Right) - _overlay.Width / 2;
        var y = Math.Clamp(center.Y, area.Top, area.Bottom) - _overlay.Height / 2;
        _overlay.Location = new(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - _overlay.Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - _overlay.Height)));
    }

    private void ShowLyrics()
    {
        _overlay.TopMost = _settings.AlwaysOnTop;
        try
        {
            _overlay.LyricsLocked = _settings.LyricsLocked;
            _overlay.WindowState = FormWindowState.Normal;
            _overlay.Show();
            if (!RefreshLyrics()) return;
            if (_settings.LyricsLocked && _settings.LockedLyricCenter is { } center) RestoreLockedPosition(center);
            Hide();
        }
        catch (Win32Exception) { HandleOverlayFailure(); }
    }

    private void ShowControlPanel()
    {
        _overlay.Hide();
        Opacity = 1;
        WindowState = FormWindowState.Normal;
        Show(); Activate();
    }

    private void HandleOverlayFailure()
    {
        ShowControlPanel();
        SetState("系统无法显示透明歌词，已打开控制窗口。", "Windows could not display transparent lyrics. The control window is open.");
    }

    private bool RefreshLyrics()
    {
        if (!_overlay.Visible) return true;
        var rows = _history.Entries;
        var latest = rows.LastOrDefault();
        return RefreshLyrics(rows, CurrentCaption(rows, latest), latest);
    }

    private static Caption? CurrentCaption(IReadOnlyList<Caption> rows, Caption? latest) =>
        latest is { Final: false } ? rows.LastOrDefault(c => c.Session == latest.Session && c.Final) ?? latest : latest;

    private string PreviousLyric(IReadOnlyList<Caption> rows, Caption? current)
    {
        if (current is null) return "";
        Caption? previous = null;
        // Walk in speech order. A slow response for an older sentence must not displace a newer translation.
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (row.Session < current.Session) break;
            if (row.Session != current.Session || row.Sequence >= current.Sequence || !row.Final) continue;
            previous ??= row;
            if (!_show.Checked) break;
            if (string.IsNullOrWhiteSpace(row.Translation) ||
                !string.Equals(row.TranslationLanguage, _settings.TargetLanguage, StringComparison.OrdinalIgnoreCase)) continue;
            var label = row.Id == previous.Id ? AppText.T("上句译文", "Previous translation") : AppText.T("前文译文", "Earlier translation");
            return label + " · " + row.Translation;
        }
        return previous is null ? "" : AppText.T("上句", "Previous") + " · " + previous.Original;
    }

    private bool RefreshLyrics(IReadOnlyList<Caption> rows, Caption? caption, Caption? latest)
    {
        if (!_overlay.Visible || _closing) return true;
        var original = caption?.Original ?? (_session.Active ? AppText.T("正在聆听…", "Listening…") : "听力字幕");
        var translation = caption is null && !_session.Active ? "Listening Subtitles" : "";
        if (caption != null && _show.Checked && caption.Translation.Length > 0)
        {
            var label = !string.Equals(caption.TranslationLanguage, _settings.TargetLanguage, StringComparison.OrdinalIgnoreCase)
                ? "[" + TranslationLanguages.DisplayName(caption.TranslationLanguage) + "] " : "";
            translation = label + caption.Translation;
        }
        var live = latest is { Final: false } && latest != caption ? latest.Original : "";
        var status = _overlayStatus;
        if (status.Length == 0 && caption is null && !_session.Active && !_busy)
            status = _settings.LyricsLocked
                ? AppText.T("歌词已锁定 · 移到字幕上可解锁", "Lyrics locked · Hover over the captions to unlock")
                : AppText.T("移到字幕上，点击开始", "Hover over the lyrics and select Start");
        if (status.Length == 0 && _show.Checked && caption?.TranslationStatus is "busy" or "error")
            status = _translated.Text;
        try
        {
            _overlay.SetContent(original, translation, live, status, _session.Active, _busy, _settings.FontSize, previous: PreviousLyric(rows, caption));
            return true;
        }
        catch (Win32Exception) { HandleOverlayFailure(); return false; }
    }

    internal Bitmap CaptureLyricFrame() => _overlay.CaptureFrame();
}
