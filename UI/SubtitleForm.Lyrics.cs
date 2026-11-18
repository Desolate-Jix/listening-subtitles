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
    }

    private void ShowLyrics()
    {
        _overlay.TopMost = _settings.AlwaysOnTop;
        try
        {
            _overlay.WindowState = FormWindowState.Normal;
            _overlay.Show();
            if (!RefreshLyrics()) return;
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
        return RefreshLyrics(rows.LastOrDefault(c => c.Final) ?? latest, latest);
    }

    private bool RefreshLyrics(Caption? caption, Caption? latest)
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
        if (status.Length == 0 && _show.Checked && caption?.TranslationStatus is "busy" or "error")
            status = _translated.Text;
        try
        {
            _overlay.SetContent(original, translation, live, status, _session.Active, _busy, _settings.FontSize);
            return true;
        }
        catch (Win32Exception) { HandleOverlayFailure(); return false; }
    }

    internal Bitmap CaptureLyricFrame() => _overlay.CaptureFrame();
}
