using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed partial class SubtitleForm : Form
{
    private readonly ListeningSettingsStore _store;
    private ListeningSettings _settings;
    private readonly TranslationClient _translation = new();
    private readonly LiveSession _session;
    private readonly CaptionHistory _history = new();
    private readonly CaptionStore _archive;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 1000 };
    private readonly Label _saved = new() { Dock = DockStyle.Bottom, Height = 25, Padding = new(16, 0, 16, 0), ForeColor = Theme.Muted, AutoEllipsis = true };
    private Func<string> _savedText = () => AppText.T("历史自动保存到本机 · 可导出双语 SRT / VTT 字幕", "History is saved locally · Export bilingual SRT / VTT subtitles");
    private Func<string> _stateText = () => AppText.T("未开始 · 播放视频后点击“开始”", "Ready · Play a video, then select Start");
    private bool _dirty, _applyingLanguage;
    private readonly Button _start, _stop, _configure, _historyButton, _clearButton;
    private readonly ComboBox _interfaceLanguage = new() { Name = "interfaceLanguage", DropDownStyle = ComboBoxStyle.DropDownList, Width = 132, Margin = new(10, 10, 4, 4) };
    private readonly RichTextBox _original = Theme.TextArea(20, Theme.Ink);
    private readonly RichTextBox _translated = Theme.TextArea(18, Theme.Mint);
    private readonly RichTextBox _live = Theme.TextArea(12, Theme.Muted);
    private readonly Label _state = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.Muted };
    private readonly ProgressBar _level = new() { Dock = DockStyle.Fill, Maximum = 100 };
    private readonly CheckBox _show = new() { AutoSize = true, Margin = new(10, 12, 6, 4) };
    private readonly CheckBox _pin = new() { AutoSize = true, Margin = new(6, 12, 6, 4) };
    private readonly NotifyIcon _tray;
    private readonly ToolStripItem _trayShow, _trayStop, _trayExit;
    private readonly System.Windows.Forms.Timer _meter = new() { Interval = 150 };
    private float _lastLevel;
    private bool _busy, _closing, _exitReady, _partialNeedsRender;

    public SubtitleForm(ListeningSettingsStore store, ListeningSettings settings)
    {
        _store = store; _settings = settings; AppText.Language = settings.InterfaceLanguage;
        _archive = new CaptionStore(store.DirectoryPath);
        try
        {
            var latest = _archive.List().FirstOrDefault();
            if (latest is not null)
            {
                _history.Load(_archive.Read(latest));
                _savedText = () => AppText.T("已恢复上次记录 · 点“历史”回看或导出", "Previous recording restored · Open History to review or export");
            }
        }
        catch { _savedText = () => AppText.T("上次记录无法读取，原文件已保留；可在“历史”中选择其他记录。", "The last recording could not be read. Its file is preserved; choose another in History."); }
        _history.Changed += () => { _dirty = true; };
        _saveTimer.Tick += (_, _) => SaveHistory(); _saveTimer.Start();
        Theme.Apply(this); MinimumSize = new(860, 430); Size = new(1180, 480);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new(Math.Max(area.Left, area.Left + (area.Width - Width) / 2), Math.Max(area.Top, area.Bottom - Height - 30));
        // Application.Run owns this control window even when the lyrics are the visible surface.
        Opacity = 0;
        TopMost = settings.AlwaysOnTop;
        _session = new LiveSession(_translation.TranslateAsync);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(10, 4, 10, 4), WrapContents = true };
        _start = Theme.Button("", async (_, _) => await StartAsync(), true);
        _stop = Theme.Button("", async (_, _) => await StopAsync()); _stop.Enabled = false;
        _configure = Theme.Button("", async (_, _) => await ConfigureAsync());
        _historyButton = Theme.Button("", (_, _) => OpenHistory());
        _clearButton = Theme.Button("", (_, _) => Clear());
        _interfaceLanguage.Items.AddRange(["简体中文", "English"]);
        controls.Controls.AddRange([_start, _stop, _configure, _historyButton, _clearButton,
            Theme.Button("A−", (_, _) => ChangeFont(-2)), Theme.Button("A+", (_, _) => ChangeFont(2)), _show, _pin, _interfaceLanguage]);
        _lyricsButton = Theme.Button("", (_, _) => ShowLyrics());
        _lyricsButton.Name = "showLyrics";
        controls.Controls.Add(_lyricsButton);
        _show.Checked = settings.ShowTranslation; _pin.Checked = settings.AlwaysOnTop;
        _show.CheckedChanged += (_, _) => { _settings = _settings with { ShowTranslation = _show.Checked }; Render(); SavePreferences(); };
        _pin.CheckedChanged += (_, _) => { TopMost = _overlay.TopMost = _pin.Checked; _settings = _settings with { AlwaysOnTop = _pin.Checked }; SavePreferences(); };
        _interfaceLanguage.SelectedIndexChanged += (_, _) =>
        {
            if (_applyingLanguage || _interfaceLanguage.SelectedIndex < 0) return;
            _settings = _settings with { InterfaceLanguage = _interfaceLanguage.SelectedIndex == 1 ? "en" : "zh-CN" };
            ApplyInterfaceLanguage(true); SavePreferences();
        };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24, 8, 24, 4), RowCount = 3, ColumnCount = 1 };
        body.RowStyles.Add(new(SizeType.Percent, 52)); body.RowStyles.Add(new(SizeType.Percent, 48)); body.RowStyles.Add(new(SizeType.Absolute, 44));
        body.Controls.Add(_original, 0, 0); body.Controls.Add(_translated, 0, 1); body.Controls.Add(_live, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, Padding = new(16, 6, 16, 6), ColumnCount = 2 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 90));
        footer.Controls.Add(_state, 0, 0); footer.Controls.Add(_level, 1, 0);
        Controls.Add(body); Controls.Add(_saved); Controls.Add(footer); Controls.Add(controls);
        _trayShow = _menu.Items.Add("", null, (_, _) => ShowLyrics());
        _trayShow.Name = "showLyrics";
        _menu.Items.Add("", null, async (_, _) => await StartAsync()).Name = "start";
        _trayStop = _menu.Items.Add("", null, async (_, _) => await StopAsync());
        _trayStop.Name = "stop";
        BuildLyricsMenu();
        _trayExit = _menu.Items.Add("", null, (_, _) => Close());
        _tray = new NotifyIcon { Icon = Theme.TrayIcon, Visible = true, ContextMenuStrip = _menu };
        _tray.DoubleClick += (_, _) => { if (CanUseMenu()) ShowLyrics(); };
        _session.CaptionChanged += caption => Post(() =>
        {
            _history.Upsert(caption);
            // Keep every transcript update in history. Paint rapid partials at the next UI tick.
            if (caption.Final) Render(); else _partialNeedsRender = true;
        });
        _session.TranslationChanged += update => Post(() => { _history.Translate(update); Render(); });
        _session.CaptureStarted += started => Post(() => _history.SetStartedAt(started));
        _session.StateChanged += status => Post(() =>
        {
            _stateText = () => status; _state.Text = status; _overlayStatus = _session.Active ? "" : status; RefreshTray();
            if (!_session.Active) { _history.MarkStopped(); Render(); }
            UpdateButtons();
        });
        _session.LevelChanged += value => Volatile.Write(ref _lastLevel, value);
        _meter.Tick += (_, _) =>
        {
            _level.Value = Math.Clamp((int)(Volatile.Read(ref _lastLevel) * 250), 0, 100);
            if (_partialNeedsRender) Render();
        };
        _meter.Start();
        FormClosing += OnClosing;
        FormClosed += (_, _) => { _saveTimer.Dispose(); _meter.Dispose(); _tray.Visible = false; _tray.Dispose(); _overlay.Dispose(); _menu.Dispose(); _translation.Dispose(); };
        ApplyInterfaceLanguage(); ApplyFont(); Render();
    }
    private void ApplyInterfaceLanguage(bool resetState = false)
    {
        AppText.Language = _settings.InterfaceLanguage;
        _applyingLanguage = true;
        try { _interfaceLanguage.SelectedIndex = _settings.InterfaceLanguage == "en" ? 1 : 0; }
        finally { _applyingLanguage = false; }
        Text = AppText.T("听力字幕", "Listening Subtitles");
        _start.Text = AppText.T("开始", "Start"); _stop.Text = AppText.T("停止", "Stop");
        _configure.Text = AppText.T("设置", "Settings"); _historyButton.Text = AppText.T("历史", "History"); _clearButton.Text = AppText.T("清空", "Clear");
        _show.Text = AppText.T("显示译文", "Show translation") + " · " + TranslationLanguages.DisplayName(_settings.TargetLanguage);
        _pin.Text = AppText.T("置顶", "Always on top");
        _interfaceLanguage.AccessibleName = AppText.T("界面语言", "Interface language");
        _lyricsButton.Text = _trayShow.Text = AppText.T("桌面歌词", "Desktop lyrics"); _trayStop.Text = AppText.T("停止转写", "Stop transcription"); _trayExit.Text = AppText.T("退出", "Exit");
        LocalizeLyricsMenu();
        if (resetState) _stateText = _session.Active
            ? () => AppText.T("正在转写 · 界面语言已更新", "Transcribing · Interface language updated")
            : () => AppText.T("已就绪 · 播放视频后点击“开始”", "Ready · Play a video, then select Start");
        _state.Text = _stateText(); _saved.Text = _savedText(); RefreshTray(); Render();
    }
    private void RefreshTray() => _tray.Text = AppText.T("听力字幕", "Listening Subtitles") + " · " +
        (_session.Active ? AppText.T("正在转写", "Transcribing") : AppText.T("已就绪", "Ready"));
    private void SetState(string chinese, string english) { _stateText = () => AppText.T(chinese, english); _state.Text = _overlayStatus = _stateText(); RefreshLyrics(); }
    private void Post(Action action)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed && !_closing) action(); })); }
        catch (InvalidOperationException) { }
    }
    private async Task StartAsync()
    {
        if (_busy || _session.Active) return;
        _overlayStatus = "";
        _busy = true; UpdateButtons();
        try
        {
            var key = Credentials.Read(_store.DirectoryPath);
            if (!SaveHistory()) { SetState("上次记录保存失败，请先在历史窗口导出，再清空或开始。", "The previous recording could not be saved. Export it from History before clearing or starting."); return; }
            _history.Clear(); Render(); await _session.StartAsync(_settings, key);
        }
        catch (Exception ex) { _stateText = () => ex.Message; _state.Text = _overlayStatus = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }
    private async Task StopAsync()
    {
        _busy = true; UpdateButtons();
        try { await _session.StopAsync(); _history.MarkStopped(); SaveHistory(); Render(); }
        catch { SetState("停止时发生错误，请退出后重新打开。", "Could not stop cleanly. Close and reopen the app."); }
        finally { _busy = false; UpdateButtons(); }
    }
    private async Task ConfigureAsync()
    {
        await StopAsync();
        using var form = new SettingsForm(_settings, new SecretStore(_store.DirectoryPath).HasKey);
        if (form.ShowDialog(ActiveSurface) != DialogResult.OK || form.Result is not { } settings) return;
        try
        {
            // Save the encrypted credential first; a failed save never activates new settings.
            if (form.NewKey.Length > 0) new SecretStore(_store.DirectoryPath).Save(form.NewKey);
            _store.Save(settings); _settings = settings; ApplyInterfaceLanguage();
            SetState("设置已保存 · 点击开始后生效", "Settings saved · Select Start to begin");
        }
        catch { SetState("设置保存失败，请检查本地文件权限后重试。", "Settings could not be saved. Check local file permissions and try again."); }
    }
    private void UpdateButtons()
    {
        _start.Enabled = !_busy && !_session.Active; _stop.Enabled = _session.Active;
        _configure.Enabled = !_busy; RefreshLyrics();
    }
    private void Clear()
    {
        if (_session.Active || _busy) { SetState("请先停止，再清空记录。", "Stop transcription before clearing the recording."); return; }
        if (!SaveHistory() && MessageBox.Show(ActiveSurface, AppText.T("自动保存失败。确定放弃当前未保存的记录？需要保留请取消，先在历史窗口导出。", "Automatic save failed. Discard the unsaved recording? Cancel to export it from History first."),
            AppText.T("清空未保存记录", "Discard unsaved recording"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (_history.Entries.Count > 0 && MessageBox.Show(ActiveSurface, AppText.T("清空当前页面？已保存的记录仍可在“历史”上方选择回看。", "Clear this page? Saved recordings remain available in History."),
            AppText.T("清空页面", "Clear page"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        _history.Clear(); Render();
    }
    private bool SaveHistory()
    {
        if (!_dirty) return true;
        if (_history.Entries.Count == 0) { _dirty = false; return true; }
        try
        {
            _archive.Save(_history); _dirty = false;
            var savedAt = DateTime.Now; var count = _history.Entries.Count;
            _savedText = () => AppText.T($"历史已自动保存 · {savedAt:HH:mm:ss} · 共 {count} 段", $"History saved · {savedAt:HH:mm:ss} · {count} segments");
            _saved.Text = _savedText(); return true;
        }
        catch
        {
            _savedText = () => AppText.T("历史保存失败 · 请打开“历史”导出到其他文件夹，退出前先保留记录", "History save failed · Export from History to another folder before exiting");
            _saved.Text = _overlayStatus = _savedText(); RefreshLyrics(); return false;
        }
    }
    private void OpenHistory()
    {
        SaveHistory();
        var targetLanguage = _settings.TargetLanguage;
        using var history = new HistoryForm(_history, _archive, () =>
        {
            var key = Credentials.Read(_store.DirectoryPath); var model = _settings.TranslationModel;
            return (text, sourceLanguages, token) => _translation.TranslateAsync(key, model, text, sourceLanguages, targetLanguage, token);
        }, targetLanguage);
        history.ShowDialog(ActiveSurface); Render();
    }
    private void ChangeFont(int delta)
    {
        _settings = _settings with { FontSize = Math.Clamp(_settings.FontSize + delta, 12, 36) }; ApplyFont(); RefreshLyrics(); SavePreferences();
    }
    private void ApplyFont()
    {
        void ResizeText(RichTextBox box, int size)
        {
            if (box.Font.Size == size) return;
            var previous = box.Font; box.Font = new Font(Font.FontFamily, size); previous.Dispose();
        }
        ResizeText(_original, _settings.FontSize); ResizeText(_translated, Math.Max(12, _settings.FontSize - 2));
    }
    private void SavePreferences()
    {
        try { _store.Save(_settings); }
        catch { SetState("本次显示已调整，但偏好保存失败。", "The display changed, but preferences could not be saved."); }
    }
    private void Render()
    {
        _partialNeedsRender = false;
        var rows = _history.Entries;
        var latest = rows.LastOrDefault();
        var caption = CurrentCaption(rows, latest);
        _original.Text = caption?.Original ?? "听见原文，再看懂意思。\nHear the original. Understand the meaning.";
        _live.Text = latest is not null && latest != caption ? AppText.T("正在听：", "Listening: ") + latest.Original : "";
        if (!_show.Checked) _translated.Text = AppText.T("译文已隐藏 · 勾选“显示译文”查看", "Translation hidden · Select Show translation to display it");
        else if (caption is null) _translated.Text = "播放视频，点击“开始”。在设置中选择输入和翻译语言。\nPlay a video and select Start. Choose input and translation languages in Settings.";
        else if (caption.Translation.Length > 0)
            _translated.Text = string.Equals(caption.TranslationLanguage, _settings.TargetLanguage, StringComparison.OrdinalIgnoreCase) ? caption.Translation :
                "[" + TranslationLanguages.DisplayName(caption.TranslationLanguage) + AppText.T(" · 历史译文；可在“历史”重译", " · saved translation; retranslate in History") + "]\n" + caption.Translation;
        else _translated.Text = caption.TranslationStatus switch
        {
            "queued" or "translating" => AppText.T("正在翻译…", "Translating…"),
            "busy" => AppText.T("本段翻译排队已满，原文保留在历史中。", "The translation queue is full. The original is preserved in History."),
            "error" => AppText.T("本段翻译失败，原文保留在历史中。", "Translation failed. The original is preserved in History."),
            "cancelled" => AppText.T("本段翻译已停止，原文保留在历史中。", "Translation stopped. The original is preserved in History."),
            _ => caption.Final ? (_settings.AutoTranslate ? "" : AppText.T("自动翻译已关闭", "Automatic translation is off")) : AppText.T("正在识别…", "Recognizing speech…")
        };
        RefreshLyrics(rows, caption, latest);
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitReady) return;
        e.Cancel = true; if (_closing) return;
        if (!SaveHistory() && MessageBox.Show(ActiveSurface, AppText.T("本机历史保存失败。确定退出并放弃尚未保存的内容？需要保留请取消，先在“历史”导出。", "Local history could not be saved. Exit and discard unsaved content? Cancel to export it from History first."),
            AppText.T("记录未保存", "Unsaved recording"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _closing = true; Enabled = false;
        try { await _session.DisposeAsync(); _history.MarkStopped(); SaveHistory(); }
        catch { SetState("资源释放失败，正在退出应用。", "Resource cleanup failed. The app is exiting."); }
        finally { BeginInvoke((Action)(() => { _exitReady = true; Close(); })); }
    }
    internal void Preview(Caption first, Caption second)
    {
        _history.Upsert(first); _history.Upsert(second); Render();
        SetState("合成字幕预览 · 未连接 API · 未采集声音", "Synthetic subtitle preview · No API connection or audio capture");
    }
}
