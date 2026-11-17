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
        Theme.Apply(this); Size = new(790, 810); MinimumSize = new(700, 670); StartPosition = FormStartPosition.CenterParent;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(22), ColumnCount = 2 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 170)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        var localized = new List<(Control Control, string Chinese, string English)>();
        var uiLanguage = new ComboBox { Name = "interfaceLanguage", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        uiLanguage.Items.AddRange([new Choice("zh-CN", "简体中文"), new Choice("en", "English")]);
        uiLanguage.SelectedIndex = current.InterfaceLanguage == "en" ? 1 : 0;
        string T(string chinese, string english) => ((Choice)uiLanguage.SelectedItem!).Code == "en" ? english : chinese;
        void SetText(Control control, string chinese, string english) { localized.Add((control, chinese, english)); control.Text = T(chinese, english); }
        void Row(string chinese, string english, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new(SizeType.AutoSize));
            var label = new Label { AutoSize = true, Margin = new(0, 8, 8, 16), MaximumSize = new(164, 0) };
            SetText(label, chinese, english); table.Controls.Add(label, 0, row);
            control.Margin = new(0, 4, 0, 12); table.Controls.Add(control, 1, row);
        }
        Label Hint(string chinese, string english)
        {
            var label = new Label { AutoSize = true, MaximumSize = new(520, 0), ForeColor = Theme.Muted };
            SetText(label, chinese, english); return label;
        }
        Row("界面语言", "Interface language", uiLanguage);
        Row("OpenAI API Key", "OpenAI API Key", _key);
        Row("", "", Hint(hasOwnKey ? "已有密钥，留空保留。由当前 Windows 用户加密。" : "填写你自己的 OpenAI API Key，由当前 Windows 用户加密保存。",
            hasOwnKey ? "A key is saved. Leave blank to keep it. Encrypted for your Windows account." : "Enter your own OpenAI API Key. It is encrypted for your Windows account."));
        var device = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var devices = new List<AudioDeviceInfo>();
        try { devices.AddRange(SystemAudioCapture.GetRenderDevices()); } catch { }
        string selectedDevice = current.OutputDeviceId;
        void LoadDevices()
        {
            if (device.SelectedItem is AudioDeviceInfo chosen) selectedDevice = chosen.Id;
            var items = new List<AudioDeviceInfo> { new("", T("自动跟随 Windows 默认输出", "Follow the default Windows output")) };
            items.AddRange(devices);
            if (!items.Any(d => d.Id == selectedDevice)) items.Add(new(selectedDevice, T("已断开的手选设备（保持原选择）", "Disconnected device (keep selection)")));
            device.DisplayMember = nameof(AudioDeviceInfo.Name); device.DataSource = items;
            device.SelectedItem = items.Single(d => d.Id == selectedDevice);
        }
        LoadDevices(); Row("电脑播放设备", "Computer audio output", device);
        var transcribe = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        transcribe.Items.AddRange(["gpt-live-transcribe", "gpt-transcribe"]); transcribe.SelectedItem = current.TranscriptionModel;
        Row("语音识别模型", "Transcription model", transcribe);
        var source = new ComboBox { Name = "sourceLanguage", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var custom = new TextBox { Name = "customSourceLanguages", Text = current.Languages, Dock = DockStyle.Fill, MaxLength = 80 };
        var common = new (string Code, string Chinese, string English)[]
        {
            ("", "自动识别", "Detect automatically"), ("en", "英语", "English"), ("zh", "中文", "Chinese"),
            ("ja", "日语", "Japanese"), ("ko", "韩语", "Korean"), ("es", "西班牙语", "Spanish"),
            ("fr", "法语", "French"), ("de", "德语", "German"), ("pt", "葡萄牙语", "Portuguese"),
            ("it", "意大利语", "Italian"), ("ru", "俄语", "Russian"), ("ar", "阿拉伯语", "Arabic"),
            ("hi", "印地语", "Hindi"), ("custom", "自定义 / 多语言", "Custom / multiple languages")
        };
        var sourceCode = common.Any(x => x.Code == current.Languages) ? current.Languages : "custom";
        void LoadSources()
        {
            if (source.SelectedItem is Choice chosen) sourceCode = chosen.Code;
            source.Items.Clear(); source.Items.AddRange(common.Select(x => new Choice(x.Code, T(x.Chinese, x.English))).ToArray());
            source.SelectedIndex = Array.FindIndex(common, x => x.Code == sourceCode);
        }
        LoadSources();
        source.SelectedIndexChanged += (_, _) => custom.Enabled = (source.SelectedItem as Choice)?.Code == "custom";
        custom.Enabled = sourceCode == "custom";
        Row("输入 / 视频语言", "Input / video language", source);
        Row("自定义语言代码", "Custom language codes", custom);
        Row("", "", Hint("多种语言用逗号分隔，如 zh,en；选“自动识别”无需填写代码。",
            "Separate multiple codes with commas, such as zh,en. Automatic detection needs no codes."));
        var target = new ComboBox { Name = "targetLanguage", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var targetCode = current.TargetLanguage;
        void LoadTargets()
        {
            if (target.SelectedItem is Choice chosen) targetCode = chosen.Code;
            target.Items.Clear();
            target.Items.AddRange(TranslationLanguages.Options.Select(x => new Choice(x.Code, T(x.ChineseName, x.EnglishName))).ToArray());
            target.SelectedItem = target.Items.Cast<Choice>().First(x => string.Equals(x.Code, targetCode, StringComparison.OrdinalIgnoreCase));
        }
        LoadTargets(); Row("翻译目标语言", "Translate into", target);
        var model = new TextBox { Text = current.TranslationModel, Dock = DockStyle.Fill, MaxLength = 128 };
        Row("翻译模型", "Translation model", model);
        var translate = new CheckBox { Checked = current.AutoTranslate, AutoSize = true, MaximumSize = new(510, 0) };
        SetText(translate, "完整语段后自动翻译为所选目标语言", "Translate completed segments into the selected language");
        Row("翻译", "Translation", translate);
        Row("", "", Hint("点“开始”后才采集电脑声音并调用付费 API。所选输出上的其他应用也会被采集。原文与译文自动加密保存在本机，可导出字幕。",
            "Audio capture and paid API calls begin only after Start. Other apps on the selected output are included. Originals and translations are encrypted locally and can be exported."));
        var error = new Label { AutoSize = true, MaximumSize = new(520, 0), ForeColor = Color.LightSalmon };
        Row("", "", error);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new(8) };
        var cancel = Theme.Button("", (_, _) => Close()); SetText(cancel, "取消", "Cancel");
        var save = Theme.Button("", (_, _) =>
        {
            var previousLanguage = AppText.Language;
            try
            {
                var selectedInterface = ((Choice)uiLanguage.SelectedItem!).Code;
                AppText.Language = selectedInterface;
                var selectedSource = ((Choice)source.SelectedItem!).Code;
                var candidate = current with { InterfaceLanguage = selectedInterface, OutputDeviceId = ((AudioDeviceInfo)device.SelectedItem!).Id,
                    TranscriptionModel = transcribe.Text, TranslationModel = model.Text.Trim(),
                    Languages = selectedSource == "custom" ? custom.Text.Trim() : selectedSource,
                    TargetLanguage = ((Choice)target.SelectedItem!).Code, AutoTranslate = translate.Checked };
                candidate.Validate();
                if (!string.IsNullOrEmpty(NewKey)) SecretStore.ValidateKey(NewKey);
                if (!hasOwnKey && string.IsNullOrEmpty(NewKey)) throw new ArgumentException(T("请填写 OpenAI API Key。", "Enter your OpenAI API Key."));
                Result = candidate; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { AppText.Language = previousLanguage; }
        }, true);
        SetText(save, "保存", "Save");
        uiLanguage.SelectedIndexChanged += (_, _) =>
        {
            Text = T("听力字幕 · 设置", "Listening Subtitles · Settings");
            foreach (var entry in localized) entry.Control.Text = T(entry.Chinese, entry.English);
            LoadDevices(); LoadSources(); LoadTargets(); error.Text = "";
        };
        Text = T("听力字幕 · 设置", "Listening Subtitles · Settings");
        footer.Controls.Add(cancel); footer.Controls.Add(save); Controls.Add(table); Controls.Add(footer);
        AcceptButton = save; CancelButton = cancel;
    }
    private sealed record Choice(string Code, string Label) { public override string ToString() => Label; }
}
