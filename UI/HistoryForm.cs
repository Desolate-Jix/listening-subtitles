using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed class HistoryForm : Form
{
    private readonly CaptionHistory _current;
    private CaptionHistory _display;
    private readonly CaptionStore? _store;
    private readonly Func<Func<string, string, CancellationToken, Task<string>>>? _createTranslator;
    private readonly string _targetLanguage;
    private readonly ListBox _list = new() { Name = "captions", Dock = DockStyle.Top, Height = 230, BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, HorizontalScrollbar = true };
    private readonly RichTextBox _detail = Theme.TextArea(16, Theme.Ink);
    private readonly ComboBox _records = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265, Margin = new(6, 10, 6, 4) };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Margin = new(4, 9, 4, 4) };
    private readonly NumericUpDown _offset = new() { Minimum = -86400, Maximum = 86400, DecimalPlaces = 3, Width = 105, Margin = new(4, 9, 4, 4) };
    private readonly CheckBox _zero = new() { Checked = true, AutoSize = true, Margin = new(10, 12, 4, 4) };
    private readonly Label _counts = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new(10, 13, 4, 4) };
    private readonly Label _notice = new() { Dock = DockStyle.Bottom, Height = 64, Padding = new(12, 4, 12, 4), AutoEllipsis = true, ForeColor = Theme.Muted };
    private readonly Button _fill;
    private CancellationTokenSource? _filling;
    private bool _refreshing;

    public HistoryForm(CaptionHistory history, CaptionStore? store = null,
        Func<Func<string, string, CancellationToken, Task<string>>>? createTranslator = null, string targetLanguage = "zh-Hans")
    {
        _current = _display = history; _store = store; _createTranslator = createTranslator; _targetLanguage = targetLanguage;
        Theme.Apply(this); Text = AppText.T("听力字幕 · 历史与字幕导出", "Listening Subtitles · History and export"); Size = new(1160, 800);
        MinimumSize = new(920, 680); StartPosition = FormStartPosition.CenterParent;
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(8, 4, 8, 4), WrapContents = true };
        _records.Items.Add(AppText.T("本次记录（实时更新）", "Current recording (live updates)"));
        try
        {
            if (store is not null)
                foreach (var saved in store.List().Where(s => !s.FileName.EndsWith("-" + history.Id.ToString("N") + ".bin", StringComparison.Ordinal)))
                    _records.Items.Add(saved);
        }
        catch { _notice.Text = AppText.T("历史目录无法读取；当前记录仍可查看和导出。", "The history folder could not be read. You can still review and export this recording."); }
        _records.SelectedIndex = 0; _records.SelectedIndexChanged += (_, _) => SelectRecord();
        _fill = Theme.Button(FillButtonText, async (_, _) => await FillTranslationAsync()); _fill.Enabled = createTranslator is not null;
        header.Controls.AddRange([_records, Theme.Button(AppText.T("保存记录", "Save recording"), (_, _) => SaveRecord()), _fill, _counts]);
        _list.SelectedIndexChanged += (_, _) => { if (!_refreshing) ShowDetail(); };
        var export = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(8, 2, 8, 6), WrapContents = true };
        _language.Items.AddRange([AppText.T("原文 + 译文", "Original + translation"), AppText.T("仅原文", "Original only"), AppText.T("仅译文", "Translation only")]); _language.SelectedIndex = 0;
        _zero.Text = AppText.T("首条字幕从 0 开始", "Start first subtitle at 0");
        export.Controls.AddRange([Theme.Button(AppText.T("导出字幕 / TXT", "Export subtitles / TXT"), (_, _) => Export()), _language,
            new Label { Text = AppText.T("整体偏移（秒）", "Offset (seconds)"), AutoSize = true, Margin = new(12, 13, 0, 4) }, _offset, _zero]);
        _zero.CheckedChanged += (_, _) => RefreshRows(); _offset.ValueChanged += (_, _) => RefreshRows();
        var hint = new Label { Dock = DockStyle.Bottom, Height = 68, Padding = new(12, 4, 12, 4), ForeColor = Theme.Muted,
            Text = AppText.T("历史、TXT、SRT、VTT 均使用相对时间；默认首条字幕为 00:00:00.000。\n取消勾选则从录音起点计时。整体偏移可对齐视频；暂停或跳播后需重新校时。",
                "History, TXT, SRT and VTT use relative time; the first subtitle starts at 00:00:00.000 by default.\nUncheck to count from recording start. Use the offset to align with video; realign after pausing or seeking.") };
        Controls.Add(_detail); Controls.Add(_list); Controls.Add(header); Controls.Add(_notice); Controls.Add(hint); Controls.Add(export);
        _display.Changed += RefreshRows;
        FormClosed += (_, _) => { _display.Changed -= RefreshRows; _filling?.Cancel(); };
        RefreshRows();
    }
    private string TargetName => TranslationLanguages.DisplayName(_targetLanguage);
    private string FillButtonText => AppText.T("补译 / 重译", "Fill / retranslate") + " · " + TargetName + " (API)";
    private void SelectRecord()
    {
        var next = _current;
        try
        {
            if (_records.SelectedItem is SavedCaptionRecord saved) { next = new CaptionHistory(); next.Load(_store!.Read(saved)); }
        }
        catch { _notice.Text = AppText.T("这份记录无法读取；请保留原文件。当前显示内容未改变。", "This recording could not be read. Preserve its file; the displayed content has not changed."); return; }
        _display.Changed -= RefreshRows; _display = next; _display.Changed += RefreshRows;
        _list.Items.Clear(); _offset.Value = 0; RefreshRows(); _notice.Text = "";
    }
    private void RefreshRows()
    {
        if (IsDisposed) return;
        var selected = (_list.SelectedItem as Row)?.Caption.Id; var top = _list.TopIndex; var rows = _display.Entries;
        _refreshing = true; _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            var origin = CaptionTimeline.Origin(_display.Snapshot(), _zero.Checked); var offset = TimeSpan.FromSeconds((double)_offset.Value);
            foreach (var row in rows) _list.Items.Add(new Row(row, CaptionTimeline.Display(row.Time, origin, offset)));
            var index = selected is null ? rows.Count - 1 : Array.FindIndex(rows.ToArray(), x => x.Id == selected);
            if (index >= 0) _list.SelectedIndex = index;
            if (selected is not null && top >= 0 && top < rows.Count) _list.TopIndex = top;
        }
        finally { _list.EndUpdate(); _refreshing = false; }
        ShowDetail(); var missing = rows.Count(IsMissing);
        _counts.Text = AppText.T($"{rows.Count} 段 · 缺 {TargetName} {missing} 段", $"{rows.Count} segments · {missing} need {TargetName}");
    }
    private bool IsMissing(Caption c) => c.Final && TranscriptTranslationQueue.NeedsTranslation(c.Original) &&
        (string.IsNullOrWhiteSpace(c.Translation) || !string.Equals(c.TranslationLanguage, _targetLanguage, StringComparison.OrdinalIgnoreCase));
    private void ShowDetail()
    {
        var caption = (_list.SelectedItem as Row)?.Caption;
        string text;
        if (caption is null) text = AppText.T("选择一段查看原文和译文。", "Select a segment to review its original and translation.");
        else
        {
            text = AppText.T("原文", "Original") + "\n" + caption.Original + "\n\n";
            if (caption.Translation.Length > 0)
            {
                text += AppText.T("译文", "Translation") + " · " + TranslationLanguages.DisplayName(caption.TranslationLanguage) + "\n" + caption.Translation;
                if (!string.Equals(caption.TranslationLanguage, _targetLanguage, StringComparison.OrdinalIgnoreCase))
                    text += "\n\n" + AppText.T($"当前目标为 {TargetName}。点击上方“补译 / 重译”可重新翻译；成功后替换这份译文，原文保留。",
                        $"The current target is {TargetName}. Use Fill / retranslate above to replace this translation after a successful request. The original is preserved.");
            }
            else text += caption.TranslationStatus switch
            {
                "queued" or "translating" => AppText.T("（正在翻译，完成后会在这里自动更新）", "(Translation is in progress and will appear here automatically.)"),
                _ => caption.Final ? AppText.T($"（本段暂无 {TargetName} 译文，可点击“补译 / 重译”）", $"(No {TargetName} translation yet. Use Fill / retranslate.)") : AppText.T("（尚未形成完整句）", "(The segment is not final yet.)")
            };
        }
        if (_detail.Text != text) _detail.Text = text;
    }
    private void SaveRecord()
    {
        if (_store is null) { _notice.Text = AppText.T("当前为测试预览；请使用导出按钮。", "This is a test preview. Use Export to save a file."); return; }
        try { _notice.Text = AppText.T("已保存到本机：", "Saved locally: ") + _store.Save(_display); }
        catch { _notice.Text = AppText.T("保存失败，请先导出到其他文件夹；当前记录仍保留在窗口中。", "Save failed. Export to another folder; the recording is still preserved in this window."); }
    }
    private async Task FillTranslationAsync()
    {
        if (_filling is not null) { _filling.Cancel(); return; }
        if (_createTranslator is null) return;
        var rows = _display.Entries.Where(c => IsMissing(c) && c.TranslationStatus is not ("queued" or "translating")).ToArray();
        if (rows.Length == 0) { _notice.Text = AppText.T("没有需要补译或重译的完整句；正在排队的译文会自动更新。", "No completed segments need translation. Queued translations will update automatically."); return; }
        var replacing = rows.Count(c => !string.IsNullOrWhiteSpace(c.Translation));
        if (replacing > 0 && MessageBox.Show(this,
            AppText.T($"将 {rows.Length} 段翻译为 {TargetName}，会产生 API 费用。其中 {replacing} 段已有其他语言译文，只有成功后才会替换。原文和失败或取消的已有译文均保留。继续？",
                $"Translate {rows.Length} segments into {TargetName} using the paid API? {replacing} existing translations in other languages will be replaced only after success. Originals and existing translations are preserved on failure or cancellation."),
            AppText.T("确认重译", "Retranslate segments"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        using var cancellation = new CancellationTokenSource();
        _filling = cancellation; _fill.Text = AppText.T("取消翻译", "Cancel translation"); _records.Enabled = false;
        var completed = 0; var failed = 0;
        try
        {
            var translate = _createTranslator(); // Read the credential only after an explicit click.
            foreach (var row in rows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _notice.Text = AppText.T($"正在翻译 {completed + failed + 1} / {rows.Length} · {TargetName} · 会产生 API 费用",
                    $"Translating {completed + failed + 1} / {rows.Length} · {TargetName} · Paid API");
                _display.Translate(new(row.Id, row.Original, "", "translating", _targetLanguage));
                try
                {
                    var result = await translate(row.Original, row.SourceLanguages, cancellation.Token).WaitAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException();
                    _display.Translate(new(row.Id, row.Original, result, "completed", _targetLanguage)); completed++;
                }
                catch (OperationCanceledException) { _display.Translate(new(row.Id, row.Original, "", "cancelled", _targetLanguage)); throw; }
                catch { _display.Translate(new(row.Id, row.Original, "", "error", _targetLanguage)); failed++; }
                // Save every finished row, including when reviewing a previous recording.
                if (_store is not null) _store.Save(_display);
            }
            _notice.Text = AppText.T($"已翻译 {completed} 段", $"Translated {completed} segments") +
                (failed > 0 ? AppText.T($" · {failed} 段失败，可稍后再试", $" · {failed} failed; try again later") : AppText.T(" · 译文已更新", " · Translations updated"));
        }
        catch (OperationCanceledException) { if (!IsDisposed) _notice.Text = AppText.T($"翻译已取消 · 已完成 {completed} 段保留", $"Translation cancelled · {completed} completed segments preserved"); }
        catch { if (!IsDisposed) _notice.Text = AppText.T("翻译或保存失败，请检查密钥、网络及保存位置。已完成内容保留在窗口中。", "Translation or save failed. Check your key, connection and save location. Completed work is preserved in the window."); }
        finally
        {
            _filling = null;
            if (!IsDisposed) { _fill.Text = FillButtonText; _records.Enabled = true; }
        }
    }
    private void Export()
    {
        var language = (CaptionLanguage)_language.SelectedIndex;
        var missing = _display.Entries.Count(IsMissing);
        if (language != CaptionLanguage.Original && missing > 0 && MessageBox.Show(this,
            AppText.T($"有 {missing} 段尚无 {TargetName} 译文，可能保留了其他语言译文。导出只包含目标语言译文，双语模式会保留所有原文。可取消后先补译 / 重译。继续导出？",
                $"{missing} segments have no {TargetName} translation, including any stored in other languages. Only target-language translations are exported; bilingual mode keeps all originals. Cancel to fill / retranslate first. Continue?"),
            AppText.T("目标语言译文尚未齐全", "Target translations are incomplete"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        using var dialog = new SaveFileDialog
        {
            Filter = AppText.T("SRT 字幕（播放器）|*.srt|WebVTT 字幕（网页 CC）|*.vtt|文本记录|*.txt", "SRT subtitles (players)|*.srt|WebVTT subtitles (web CC)|*.vtt|Text transcript|*.txt"),
            FileName = AppText.T("听力字幕-", "ListeningSubtitles-") + _display.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"),
            DefaultExt = "srt", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var offset = TimeSpan.FromSeconds((double)_offset.Value);
            var content = dialog.FilterIndex == 3 ? _display.Export(_zero.Checked, offset, _targetLanguage, language) :
                CaptionExporter.Export(_display.Snapshot(), dialog.FilterIndex == 2 ? CaptionFormat.Vtt : CaptionFormat.Srt,
                    language, offset, _zero.Checked, _targetLanguage);
            CaptionExporter.Write(dialog.FileName, content); _notice.Text = AppText.T("导出成功：", "Exported: ") + dialog.FileName;
        }
        catch (InvalidOperationException ex) { _notice.Text = ex.Message; }
        catch { _notice.Text = AppText.T("导出失败，请换一个可写的文件夹；原记录仍然保留。", "Export failed. Choose a writable folder; the original recording is preserved."); }
    }
    private sealed record Row(Caption Caption, string Timecode)
    {
        public override string ToString() => $"[{Timecode}] " +
            (Caption.Translation.Length > 0 ? "✓ [" + TranslationLanguages.DisplayName(Caption.TranslationLanguage) + "] " : "") + Caption.Original.Replace("\n", " ");
    }
}
