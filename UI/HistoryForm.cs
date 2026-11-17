using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed class HistoryForm : Form
{
    private readonly CaptionHistory _current;
    private CaptionHistory _display;
    private readonly CaptionStore? _store;
    private readonly Func<Func<string, CancellationToken, Task<string>>>? _createTranslator;
    private readonly ListBox _list = new() { Name = "captions", Dock = DockStyle.Top, Height = 230, BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _detail = Theme.TextArea(16, Theme.Ink);
    private readonly ComboBox _records = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Margin = new(6, 10, 6, 4) };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125, Margin = new(4, 9, 4, 4) };
    private readonly NumericUpDown _offset = new() { Minimum = -86400, Maximum = 86400, DecimalPlaces = 3, Width = 105, Margin = new(4, 9, 4, 4) };
    private readonly CheckBox _zero = new() { Text = "首条字幕从 0 开始", Checked = true, AutoSize = true, Margin = new(10, 12, 4, 4) };
    private readonly Label _counts = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new(10, 13, 4, 4) };
    private readonly Label _notice = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new(12, 4, 12, 4), AutoEllipsis = true, ForeColor = Theme.Muted };
    private readonly Button _fill;
    private CancellationTokenSource? _filling;
    private bool _refreshing;

    public HistoryForm(CaptionHistory history, CaptionStore? store = null,
        Func<Func<string, CancellationToken, Task<string>>>? createTranslator = null)
    {
        _current = _display = history; _store = store; _createTranslator = createTranslator;
        Theme.Apply(this); Text = "听力字幕 · 历史与字幕导出"; Size = new(1000, 720);
        MinimumSize = new(860, 600); StartPosition = FormStartPosition.CenterParent;
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, Padding = new(8, 4, 8, 4), WrapContents = false };
        _records.Items.Add("本次记录（实时更新）");
        try
        {
            if (store is not null)
                foreach (var saved in store.List().Where(s => !s.FileName.EndsWith("-" + history.Id.ToString("N") + ".bin", StringComparison.Ordinal)))
                    _records.Items.Add(saved);
        }
        catch { _notice.Text = "历史目录无法读取；当前记录仍可查看和导出。"; }
        _records.SelectedIndex = 0;
        _records.SelectedIndexChanged += (_, _) => SelectRecord();
        _fill = Theme.Button("补齐中文（API）", async (_, _) => await FillChineseAsync());
        _fill.Enabled = createTranslator is not null;
        header.Controls.AddRange([_records, Theme.Button("保存记录", (_, _) => SaveRecord()), _fill, _counts]);
        _list.SelectedIndexChanged += (_, _) => { if (!_refreshing) ShowDetail(); };
        var export = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new(8, 2, 8, 2), WrapContents = false };
        _language.Items.AddRange(["原文 + 中文", "仅原文", "仅中文"]); _language.SelectedIndex = 0;
        export.Controls.AddRange([Theme.Button("导出字幕 / TXT", (_, _) => Export()), _language,
            new Label { Text = "整体偏移（秒）", AutoSize = true, Margin = new(12, 13, 0, 4) }, _offset, _zero]);
        _zero.CheckedChanged += (_, _) => RefreshRows();
        _offset.ValueChanged += (_, _) => RefreshRows();
        var hint = new Label { Dock = DockStyle.Bottom, Height = 50, Padding = new(12, 4, 12, 4), ForeColor = Theme.Muted,
            Text = "历史、TXT、SRT、VTT 均使用相对时间；默认首条字幕为 00:00:00.000。\n取消勾选则从录音起点计时。整体偏移可对齐视频；暂停或跳播后需重新校时。" };
        Controls.Add(_detail); Controls.Add(_list); Controls.Add(header);
        Controls.Add(_notice); Controls.Add(hint); Controls.Add(export);
        _display.Changed += RefreshRows;
        FormClosed += (_, _) => { _display.Changed -= RefreshRows; _filling?.Cancel(); };
        RefreshRows();
    }
    private void SelectRecord()
    {
        var next = _current;
        try
        {
            if (_records.SelectedItem is SavedCaptionRecord saved)
            { next = new CaptionHistory(); next.Load(_store!.Read(saved)); }
        }
        catch { _notice.Text = "这份记录无法读取；请保留原文件。当前显示内容未改变。"; return; }
        _display.Changed -= RefreshRows; _display = next; _display.Changed += RefreshRows;
        _list.Items.Clear(); _offset.Value = 0; RefreshRows(); _notice.Text = "";
    }
    private void RefreshRows()
    {
        if (IsDisposed) return;
        var selected = (_list.SelectedItem as Row)?.Caption.Id;
        var top = _list.TopIndex;
        var rows = _display.Entries;
        _refreshing = true; _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            var origin = CaptionTimeline.Origin(_display.Snapshot(), _zero.Checked);
            var offset = TimeSpan.FromSeconds((double)_offset.Value);
            foreach (var row in rows) _list.Items.Add(new Row(row, CaptionTimeline.Display(row.Time, origin, offset)));
            var index = selected is null ? rows.Count - 1 : Array.FindIndex(rows.ToArray(), x => x.Id == selected);
            if (index >= 0) _list.SelectedIndex = index;
            if (selected is not null && top >= 0 && top < rows.Count) _list.TopIndex = top;
        }
        finally { _list.EndUpdate(); _refreshing = false; }
        ShowDetail();
        var missing = rows.Count(IsMissing);
        _counts.Text = $"{rows.Count} 段 · 缺中文 {missing} 段";
    }
    private static bool IsMissing(Caption c) => c.Final && string.IsNullOrWhiteSpace(c.Chinese) &&
        TranscriptTranslationQueue.NeedsTranslation(c.Original);
    private void ShowDetail()
    {
        var caption = (_list.SelectedItem as Row)?.Caption;
        var text = caption is null ? "选择一段查看原文和中文。" : caption.Original + "\n\n" +
            (caption.Chinese.Length > 0 ? caption.Chinese : caption.TranslationStatus switch
            {
                "queued" or "translating" => "（正在翻译，完成后会在这里自动更新）",
                _ => caption.Final ? "（本段暂无中文，可点击“补齐中文（API）”）" : "（尚未形成完整句）"
            });
        if (_detail.Text != text) _detail.Text = text;
    }
    private void SaveRecord()
    {
        if (_store is null) { _notice.Text = "当前为测试预览；请使用导出按钮。"; return; }
        try { _notice.Text = "已保存到本机：" + _store.Save(_display); }
        catch { _notice.Text = "保存失败，请先导出到其他文件夹；当前记录仍保留在窗口中。"; }
    }
    private async Task FillChineseAsync()
    {
        if (_filling is not null) { _filling.Cancel(); return; }
        if (_createTranslator is null) return;
        var rows = _display.Entries.Where(c => IsMissing(c) && c.TranslationStatus is not ("queued" or "translating")).ToArray();
        if (rows.Length == 0) { _notice.Text = "没有可补译的完整句；正在排队的译文会自动更新。"; return; }
        using var cancellation = new CancellationTokenSource();
        _filling = cancellation; _fill.Text = "取消补译"; _records.Enabled = false;
        var completed = 0; var failed = 0;
        try
        {
            var translate = _createTranslator(); // Read the credential only after an explicit click.
            foreach (var row in rows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _notice.Text = $"正在补译 {completed + failed + 1} / {rows.Length} · 会产生 API 费用";
                _display.Translate(new(row.Id, row.Original, "", "translating"));
                try
                {
                    var result = await translate(row.Original, cancellation.Token).WaitAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException();
                    _display.Translate(new(row.Id, row.Original, result, "completed")); completed++;
                }
                catch (OperationCanceledException)
                { _display.Translate(new(row.Id, row.Original, "", "cancelled")); throw; }
                catch { _display.Translate(new(row.Id, row.Original, "", "error")); failed++; }
                // Save every finished row, including when reviewing a previous recording.
                if (_store is not null) _store.Save(_display);
            }
            _notice.Text = $"已补译 {completed} 段" + (failed > 0 ? $" · {failed} 段失败，可稍后再试" : " · 中文已更新");
        }
        catch (OperationCanceledException) { if (!IsDisposed) _notice.Text = $"补译已取消 · 已完成 {completed} 段保留"; }
        catch { if (!IsDisposed) _notice.Text = "补译或保存失败，请检查密钥、网络及保存位置。已完成内容保留在窗口中。"; }
        finally
        {
            _filling = null;
            if (!IsDisposed) { _fill.Text = "补齐中文（API）"; _records.Enabled = true; }
        }
    }
    private void Export()
    {
        var language = (CaptionLanguage)_language.SelectedIndex;
        if (language != CaptionLanguage.Original && _display.Entries.Any(IsMissing) &&
            MessageBox.Show(this, "部分语段还没有中文。现在导出将保留已有译文；也可以取消后先点“补齐中文”。继续导出？",
                "中文尚未齐全", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "SRT 字幕（播放器）|*.srt|WebVTT 字幕（网页 CC）|*.vtt|双语文本记录|*.txt",
            FileName = "听力字幕-" + _display.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"),
            DefaultExt = "srt", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var offset = TimeSpan.FromSeconds((double)_offset.Value);
            var content = dialog.FilterIndex == 3 ? _display.Export(_zero.Checked, offset) :
                CaptionExporter.Export(_display.Snapshot(), dialog.FilterIndex == 2 ? CaptionFormat.Vtt : CaptionFormat.Srt,
                    language, offset, _zero.Checked);
            CaptionExporter.Write(dialog.FileName, content);
            _notice.Text = "导出成功：" + dialog.FileName;
        }
        catch (InvalidOperationException ex) { _notice.Text = ex.Message; }
        catch { _notice.Text = "导出失败，请换一个可写的文件夹；原记录仍然保留。"; }
    }
    private sealed record Row(Caption Caption, string Timecode)
    {
        public override string ToString() => $"[{Timecode}] " +
            (Caption.Chinese.Length > 0 ? "✓ " : "") + Caption.Original.Replace("\n", " ");
    }
}
