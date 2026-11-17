using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed class SubtitleForm : Form
{
    private readonly ListeningSettingsStore _store;
    private ListeningSettings _settings;
    private readonly TranslationClient _translation = new();
    private readonly LiveSession _session;
    private readonly CaptionHistory _history = new();
    private readonly CaptionStore _archive;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 1000 };
    private readonly Label _saved = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new(16, 0, 16, 0), ForeColor = Theme.Muted,
        Text = "历史自动保存到本机 · 可导出双语 SRT / VTT 字幕" };
    private bool _dirty;
    private readonly Button _start, _stop, _configure;
    private readonly RichTextBox _original = Theme.TextArea(20, Theme.Ink);
    private readonly RichTextBox _chinese = Theme.TextArea(18, Theme.Mint);
    private readonly RichTextBox _live = Theme.TextArea(12, Theme.Muted);
    private readonly Label _state = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.Muted, Text = "未开始 · 播放视频后点击“开始”" };
    private readonly ProgressBar _level = new() { Dock = DockStyle.Fill, Maximum = 100 };
    private readonly CheckBox _show = new() { Text = "显示中文", AutoSize = true, Margin = new(10, 12, 6, 4) };
    private readonly CheckBox _pin = new() { Text = "置顶", AutoSize = true, Margin = new(6, 12, 6, 4) };
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _meter = new() { Interval = 150 };
    private float _lastLevel;
    private bool _busy, _closing, _exitReady;
    public SubtitleForm(ListeningSettingsStore store, ListeningSettings settings)
    {
        _store = store; _settings = settings;
        _archive = new CaptionStore(store.DirectoryPath);
        try
        {
            var latest = _archive.List().FirstOrDefault();
            if (latest is not null) { _history.Load(_archive.Read(latest)); _saved.Text = "已恢复上次记录 · 点“历史”回看或导出"; }
        }
        catch { _saved.Text = "上次记录无法读取，原文件已保留；可在“历史”中选择其他记录。"; }
        _history.Changed += () => { _dirty = true; };
        _saveTimer.Tick += (_, _) => SaveHistory();
        _saveTimer.Start();
        Theme.Apply(this); Text = "听力字幕"; MinimumSize = new(760, 340); Size = new(1060, 370);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new(Math.Max(area.Left, area.Left + (area.Width - Width) / 2), Math.Max(area.Top, area.Bottom - Height - 30));
        TopMost = settings.AlwaysOnTop;
        _session = new LiveSession(_translation.TranslateAsync);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new(10, 4, 4, 4), WrapContents = false, AutoScroll = true };
        _start = Theme.Button("开始", async (_, _) => await StartAsync(), true);
        _stop = Theme.Button("停止", async (_, _) => await StopAsync());
        _stop.Enabled = false;
        _configure = Theme.Button("设置", async (_, _) => await ConfigureAsync());
        controls.Controls.AddRange([_start, _stop, _configure, Theme.Button("历史", (_, _) => OpenHistory()),
            Theme.Button("清空", (_, _) => Clear()), Theme.Button("A−", (_, _) => ChangeFont(-2)), Theme.Button("A+", (_, _) => ChangeFont(2)), _show, _pin]);
        _show.Checked = settings.ShowChinese; _pin.Checked = settings.AlwaysOnTop;
        _show.CheckedChanged += (_, _) => { _settings = _settings with { ShowChinese = _show.Checked }; Render(); SavePreferences(); };
        _pin.CheckedChanged += (_, _) => { TopMost = _pin.Checked; _settings = _settings with { AlwaysOnTop = _pin.Checked }; SavePreferences(); };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24, 8, 24, 4), RowCount = 3, ColumnCount = 1 };
        body.RowStyles.Add(new(SizeType.Percent, 55)); body.RowStyles.Add(new(SizeType.Percent, 45)); body.RowStyles.Add(new(SizeType.Absolute, 42));
        body.Controls.Add(_original, 0, 0); body.Controls.Add(_chinese, 0, 1); body.Controls.Add(_live, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, Padding = new(16, 6, 16, 6), ColumnCount = 2 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 90));
        footer.Controls.Add(_state, 0, 0); footer.Controls.Add(_level, 1, 0);
        Controls.Add(body); Controls.Add(_saved); Controls.Add(footer); Controls.Add(controls);
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示字幕窗", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("停止转写", null, async (_, _) => await StopAsync());
        menu.Items.Add("退出", null, (_, _) => Close());
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "听力字幕 · 未开始", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        _session.CaptionChanged += caption => Post(() => { _history.Upsert(caption); Render(); });
        _session.TranslationChanged += update => Post(() => { _history.Translate(update); Render(); });
        _session.CaptureStarted += started => Post(() => _history.SetStartedAt(started));
        _session.StateChanged += status => Post(() =>
        {
            _state.Text = status; _tray.Text = _session.Active ? "听力字幕 · 正在转写" : "听力字幕 · 已停止";
            if (!_session.Active) { _history.MarkStopped(); Render(); }
            UpdateButtons();
        });
        _session.LevelChanged += value => Volatile.Write(ref _lastLevel, value);
        _meter.Tick += (_, _) => _level.Value = Math.Clamp((int)(Volatile.Read(ref _lastLevel) * 250), 0, 100);
        _meter.Start();
        FormClosing += OnClosing;
        FormClosed += (_, _) => { _saveTimer.Dispose(); _meter.Dispose(); _tray.Visible = false; _tray.Dispose(); _translation.Dispose(); };
        ApplyFont(); Render();
    }
    private void Post(Action action)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed && !_closing) action(); })); }
        catch (InvalidOperationException) { }
    }
    private async Task StartAsync()
    {
        if (_busy || _session.Active) return;
        _busy = true; UpdateButtons();
        try
        {
            var key = Credentials.Read(_store.DirectoryPath);
            if (!SaveHistory()) { _state.Text = "上次记录保存失败，请先在历史窗口导出，再清空或开始。"; return; }
            _history.Clear(); Render();
            await _session.StartAsync(_settings, key);
        }
        catch (Exception ex) { _state.Text = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }
    private async Task StopAsync()
    {
        _busy = true; UpdateButtons();
        try { await _session.StopAsync(); _history.MarkStopped(); SaveHistory(); Render(); }
        catch { _state.Text = "停止时发生错误，请退出后重新打开。"; }
        finally { _busy = false; UpdateButtons(); }
    }
    private async Task ConfigureAsync()
    {
        await StopAsync();
        using var form = new SettingsForm(_settings, new SecretStore(_store.DirectoryPath).HasKey);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result is not { } settings) return;
        try
        {
            // Save the encrypted credential first; a failed save never activates new settings.
            if (form.NewKey.Length > 0) new SecretStore(_store.DirectoryPath).Save(form.NewKey);
            _store.Save(settings); _settings = settings;
            _state.Text = "设置已保存 · 点击开始后生效"; Render();
        }
        catch { _state.Text = "设置保存失败，请检查本地文件权限后重试。"; }
    }
    private void UpdateButtons()
    {
        _start.Enabled = !_busy && !_session.Active; _stop.Enabled = _session.Active;
        _configure.Enabled = !_busy; // Opening settings intentionally stops the current stream.
    }
    private void Clear()
    {
        if (_session.Active || _busy) { _state.Text = "请先停止，再清空记录。"; return; }
        if (!SaveHistory() && MessageBox.Show(this, "自动保存失败。确定放弃当前未保存的记录？需要保留请取消，先在历史窗口导出。",
            "清空未保存记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (_history.Entries.Count > 0 && MessageBox.Show(this, "清空当前页面？已保存的记录仍可在“历史”上方选择回看。", "清空页面", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        _history.Clear(); Render();
    }
    private bool SaveHistory()
    {
        if (!_dirty) return true;
        if (_history.Entries.Count == 0) { _dirty = false; return true; }
        try
        {
            _archive.Save(_history); _dirty = false;
            _saved.Text = $"历史已自动保存 · {DateTime.Now:HH:mm:ss} · 共 {_history.Entries.Count} 段";
            return true;
        }
        catch
        {
            _saved.Text = "历史保存失败 · 请打开“历史”导出到其他文件夹，退出前先保留记录";
            return false;
        }
    }
    private void OpenHistory()
    {
        SaveHistory();
        using var history = new HistoryForm(_history, _archive, () =>
        {
            var key = Credentials.Read(_store.DirectoryPath);
            var model = _settings.TranslationModel;
            return (text, token) => _translation.TranslateAsync(key, model, text, token);
        });
        history.ShowDialog(this);
        Render();
    }
    private void ChangeFont(int delta)
    {
        _settings = _settings with { FontSize = Math.Clamp(_settings.FontSize + delta, 12, 36) };
        ApplyFont(); SavePreferences();
    }
    private void ApplyFont()
    {
        void ResizeText(RichTextBox box, int size)
        {
            if (box.Font.Size == size) return;
            var previous = box.Font;
            box.Font = new Font(Font.FontFamily, size);
            previous.Dispose();
        }
        ResizeText(_original, _settings.FontSize);
        ResizeText(_chinese, Math.Max(12, _settings.FontSize - 2));
    }
    private void SavePreferences()
    {
        try { _store.Save(_settings); }
        catch { _state.Text = "本次显示已调整，但偏好保存失败。"; }
    }
    private void Render()
    {
        var latest = _history.Latest;
        var caption = _history.Entries.LastOrDefault(c => c.Final) ?? latest;
        _original.Text = caption?.Original ?? "听见原文，再看懂意思。";
        _live.Text = latest is not null && latest != caption ? "正在听：" + latest.Original : "";
        if (!_show.Checked) _chinese.Text = "中文已隐藏 · 勾选“显示中文”查看译文";
        else if (caption is null) _chinese.Text = "播放没有字幕的视频，点击“开始”。原文和中文会显示在这里。";
        else if (caption.Chinese.Length > 0) _chinese.Text = caption.Chinese;
        else _chinese.Text = caption.TranslationStatus switch
        {
            "queued" or "translating" => "正在翻译…",
            "busy" => "本段翻译排队已满，原文保留在历史中。",
            "error" => "本段翻译失败，原文保留在历史中。",
            "cancelled" => "本段翻译已停止，原文保留在历史中。",
            _ => caption.Final ? (_settings.AutoTranslate ? "" : "自动翻译已关闭") : "正在识别…"
        };
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitReady) return;
        e.Cancel = true;
        if (_closing) return;
        if (!SaveHistory())
        {
            if (MessageBox.Show(this, "本机历史保存失败。确定退出并放弃尚未保存的内容？需要保留请取消，先在“历史”导出。",
                "记录未保存", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        _closing = true; Enabled = false;
        try { await _session.DisposeAsync(); _history.MarkStopped(); SaveHistory(); }
        catch { _state.Text = "资源释放失败，正在退出应用。"; }
        finally { BeginInvoke((Action)(() => { _exitReady = true; Close(); })); }
    }
    internal void Preview(Caption first, Caption second)
    {
        _history.Upsert(first); _history.Upsert(second); Render();
        _state.Text = "合成字幕预览 · 未连接 API · 未采集声音";
    }
}
