using ListeningSubtitles.Audio;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed class SettingsForm : Form
{
    public ListeningSettings? Result { get; private set; }
    public string NewKey => _key.Text.Trim();
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, MaxLength = 8192 };
    public SettingsForm(ListeningSettings current, bool hasOwnKey)
    {
        Theme.Apply(this); Text = "听力字幕 · 设置"; Size = new(700, 610); MinimumSize = new(620, 550); StartPosition = FormStartPosition.CenterParent;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(22), ColumnCount = 2 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 130)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string title, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new(0, 8, 4, 16) }, 0, row);
            control.Margin = new(0, 4, 0, 12); table.Controls.Add(control, 1, row);
        }
        Row("OpenAI API Key", _key);
        Row("", new Label { Text = hasOwnKey ? "已有密钥，留空保留。由当前 Windows 用户加密。" : "填写你自己的 OpenAI API Key，由当前 Windows 用户加密保存。", AutoSize = true, MaximumSize = new(440, 0) });
        var device = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var devices = new List<AudioDeviceInfo> { new("", "自动跟随 Windows 默认输出") };
        try { devices.AddRange(SystemAudioCapture.GetRenderDevices()); } catch { }
        if (!devices.Any(d => d.Id == current.OutputDeviceId)) devices.Add(new(current.OutputDeviceId, "已断开的手选设备（保持原选择）"));
        device.DisplayMember = nameof(AudioDeviceInfo.Name); device.DataSource = devices; device.SelectedItem = devices.Single(d => d.Id == current.OutputDeviceId);
        Row("电脑播放设备", device);
        var transcribe = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        transcribe.Items.AddRange(["gpt-live-transcribe", "gpt-transcribe"]); transcribe.SelectedItem = current.TranscriptionModel;
        Row("语音识别模型", transcribe);
        var languages = new TextBox { Text = current.Languages, Dock = DockStyle.Fill, MaxLength = 80 };
        Row("视频语言", languages);
        Row("", new Label { Text = "英语填 en；混合填 zh,en；留空自动识别。", AutoSize = true });
        var model = new TextBox { Text = current.TranslationModel, Dock = DockStyle.Fill, MaxLength = 128 };
        Row("中文翻译模型", model);
        var translate = new CheckBox { Text = "完整语段后自动翻译为中文", Checked = current.AutoTranslate, AutoSize = true };
        Row("翻译", translate);
        Row("", new Label { Text = "点“开始”后才采集电脑声音并调用付费 API。系统声音包含所选输出上的其他应用。原文与译文自动加密保存在本机，可导出双语字幕。", MaximumSize = new(440, 0), AutoSize = true, ForeColor = Theme.Muted });
        var error = new Label { AutoSize = true, MaximumSize = new(440, 0), ForeColor = Color.LightSalmon };
        Row("", error);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new(8) };
        var cancel = Theme.Button("取消", (_, _) => Close());
        var save = Theme.Button("保存", (_, _) =>
        {
            try
            {
                var candidate = current with { OutputDeviceId = ((AudioDeviceInfo)device.SelectedItem!).Id,
                    TranscriptionModel = transcribe.Text, TranslationModel = model.Text.Trim(), Languages = languages.Text.Trim(), AutoTranslate = translate.Checked };
                candidate.Validate();
                if (!string.IsNullOrEmpty(NewKey)) SecretStore.ValidateKey(NewKey);
                if (!hasOwnKey && string.IsNullOrEmpty(NewKey)) throw new ArgumentException("请填写 OpenAI API Key。");
                Result = candidate; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }, true);
        footer.Controls.Add(cancel); footer.Controls.Add(save); Controls.Add(table); Controls.Add(footer);
        AcceptButton = save; CancelButton = cancel;
    }
}
