using ListeningSubtitles.Audio;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

public sealed class SettingsForm : Form
{
    public ListeningSettings? Result { get; private set; }
    public string NewKey => _key.Text.Trim();

    private readonly ListeningSettings _current;
    private readonly bool _hasOwnKey;
    private readonly List<(Control Control, string Chinese, string English)> _localized = new();
    private readonly List<Font> _fonts = new();
    private readonly Image _brandImage = Theme.AppIcon.ToBitmap();
    private readonly List<AudioDeviceInfo> _devices = new();
    private readonly TextBox _key = new() { Name = "apiKey", UseSystemPasswordChar = true, MaxLength = 8192 };
    private readonly TextBox _custom = new() { Name = "customSourceLanguages", MaxLength = 80 };
    private readonly TextBox _model = new() { Name = "translationModel", MaxLength = 128 };
    private readonly ComboBox _uiLanguage = LanguageCombo("interfaceLanguage");
    private readonly ComboBox _source = LanguageCombo("sourceLanguage");
    private readonly ComboBox _target = LanguageCombo("targetLanguage");
    private readonly ComboBox _device = LanguageCombo("outputDevice");
    private readonly ComboBox _transcribe = LanguageCombo("transcriptionModel");
    private readonly CheckBox _translate = new() { Name = "autoTranslate", AutoSize = true, Dock = DockStyle.Top, UseVisualStyleBackColor = false };
    private readonly SettingsNavigation _navigation = new() { Name = "settingsNavigation", Dock = DockStyle.Top, Height = 186, TabIndex = 0 };
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, TabIndex = 1 };
    private readonly Panel[] _pages = new Panel[3];
    private readonly Label _heading = new() { Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false, Margin = new(0, 0, 0, 8) };
    private readonly Label _description = new() { Dock = DockStyle.Top, AutoSize = true, ForeColor = SettingsPalette.Muted, Margin = new(0, 0, 0, 18) };
    private readonly Label _error = new() { Name = "settingsError", Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false, AccessibleRole = AccessibleRole.Alert, Margin = new(0, 0, 0, 10) };
    private Control _customField = null!;
    private Button _save = null!, _cancel = null!;
    private bool _localizing;
    private int _selectedPage;
    private string _selectedDevice, _sourceCode, _targetCode;
    private string _errorMessage = "";
    private Control? _invalidControl;

    private static readonly (string Code, string Chinese, string English)[] SourceOptions =
    [
        ("", "自动识别", "Detect automatically"), ("en", "英语", "English"), ("zh", "中文", "Chinese"),
        ("ja", "日语", "Japanese"), ("ko", "韩语", "Korean"), ("es", "西班牙语", "Spanish"),
        ("fr", "法语", "French"), ("de", "德语", "German"), ("pt", "葡萄牙语", "Portuguese"),
        ("it", "意大利语", "Italian"), ("ru", "俄语", "Russian"), ("ar", "阿拉伯语", "Arabic"),
        ("hi", "印地语", "Hindi"), ("custom", "自定义 / 多语言", "Custom / multiple languages")
    ];

    public SettingsForm(ListeningSettings current, bool hasOwnKey)
    {
        _current = current; _hasOwnKey = hasOwnKey;
        _selectedDevice = current.OutputDeviceId; _targetCode = current.TargetLanguage;
        _sourceCode = SourceOptions.Any(x => x.Code == current.Languages) ? current.Languages : "custom";
        Theme.Apply(this);
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SettingsPalette.Canvas; ForeColor = SettingsPalette.Ink;
        ClientSize = new(920, 600); MinimumSize = new(660, 450); StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        _key.BackColor = _custom.BackColor = _model.BackColor = SettingsPalette.Surface;
        _key.ForeColor = _custom.ForeColor = _model.ForeColor = SettingsPalette.Ink;
        _custom.Text = current.Languages; _model.Text = current.TranslationModel;
        _uiLanguage.Items.AddRange([new Choice("zh-CN", "简体中文"), new Choice("en", "English")]);
        _uiLanguage.SelectedIndex = current.InterfaceLanguage == "en" ? 1 : 0;
        _transcribe.Items.AddRange(["gpt-live-transcribe", "gpt-transcribe"]); _transcribe.SelectedItem = current.TranscriptionModel;
        _translate.Checked = current.AutoTranslate;
        try { _devices.AddRange(SystemAudioCapture.GetRenderDevices()); } catch { }
        LoadChoices();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 208)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = SettingsPalette.Sidebar, Padding = new(14, 20, 14, 16), TabIndex = 0, ColumnCount = 1, RowCount = 2 };
        sidebar.ColumnStyles.Add(new(SizeType.Percent, 100)); sidebar.RowStyles.Add(new(SizeType.AutoSize)); sidebar.RowStyles.Add(new(SizeType.Percent, 100));
        var brand = BoundLabel("听力字幕", "Listening Subtitles", 10, true); brand.TextAlign = ContentAlignment.MiddleLeft;
        var brandRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new(4, 0, 0, 12) };
        brandRow.ColumnStyles.Add(new(SizeType.Absolute, 40)); brandRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        brandRow.MinimumSize = new(0, 32);
        brandRow.Paint += (_, e) =>
        {
            var side = Math.Max(1, (int)Math.Round(30 * DeviceDpi / 96f));
            e.Graphics.DrawImage(_brandImage, new Rectangle(0, Math.Max(0, (brandRow.Height - side) / 2), side, side));
        };
        brand.Dock = DockStyle.Fill; brandRow.Controls.Add(brand, 1, 0);
        var sidebarTitle = BoundLabel("设置", "Settings", 22, true); sidebarTitle.Margin = new(8, 0, 0, 18);
        var sidebarTop = Stack(); sidebarTop.Padding = new(0, 0, 0, 6); Add(sidebarTop, brandRow); Add(sidebarTop, sidebarTitle);
        sidebar.Controls.Add(sidebarTop, 0, 0); _navigation.Dock = DockStyle.Fill; sidebar.Controls.Add(_navigation, 0, 1);
        sidebar.Resize += (_, _) =>
        {
            var roomy = sidebar.Height >= 380 * DeviceDpi / 96f;
            sidebarTitle.Visible = roomy;
        };

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new(28, 24, 22, 0), TabIndex = 1 };
        content.ColumnStyles.Add(new(SizeType.Percent, 100)); content.RowStyles.Add(new(SizeType.AutoSize)); content.RowStyles.Add(new(SizeType.Percent, 100));
        var header = Stack(); _heading.Font = StyledFont(20, true); Add(header, _heading); Add(header, _description);
        content.Controls.Add(header, 0, 0); content.Controls.Add(_pageHost, 0, 1);
        _pageHost.Resize += (_, _) => ResizeWrappedText();
        _pages[0] = CreatePage("settingsLanguagesPage"); _pages[1] = CreatePage("settingsAudioPage"); _pages[2] = CreatePage("settingsConnectionPage");
        BuildLanguagePage(); BuildAudioPage(); BuildConnectionPage();
        foreach (var page in _pages) _pageHost.Controls.Add(page);
        layout.Controls.Add(sidebar, 0, 0); layout.Controls.Add(content, 1, 0);
        var footer = BuildFooter(); layout.Controls.Add(footer, 0, 1); layout.SetColumnSpan(footer, 2);
        Controls.Add(layout);
        _navigation.SelectedIndexChanged += (_, _) => { if (!_localizing && _navigation.SelectedIndex >= 0) SelectPage(_navigation.SelectedIndex); };
        _source.SelectedIndexChanged += (_, _) => { if (!_localizing) UpdateCustomField(); };
        _uiLanguage.SelectedIndexChanged += (_, _) => { if (!_localizing) { _errorMessage = ""; ApplyLanguage(); } };
        ApplyLanguage();
        Shown += (_, _) => { FitWorkingArea(); ResizeWrappedText(); };
    }

    private string T(string chinese, string english) => (_uiLanguage.SelectedItem as Choice)?.Code == "en" ? english : chinese;
    private Font StyledFont(float size, bool bold = false)
    {
        var font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular); _fonts.Add(font); return font;
    }
    private Label BoundLabel(string chinese, string english, float size = 10, bool bold = false)
    {
        var label = new Label { AutoSize = true, Dock = DockStyle.Top, ForeColor = SettingsPalette.Ink, Margin = Padding.Empty, UseMnemonic = false };
        if (size != 10 || bold) label.Font = StyledFont(size, bold);
        Bind(label, chinese, english); return label;
    }
    private void Bind(Control control, string chinese, string english)
    {
        _localized.Add((control, chinese, english)); control.Text = T(chinese, english);
    }
    private static ComboBox LanguageCombo(string name)
    {
        var combo = new ComboBox { Name = name, Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, BackColor = SettingsPalette.Surface, ForeColor = SettingsPalette.Ink,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 27, IntegralHeight = false, DropDownHeight = 260, Margin = Padding.Empty };
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font,
                Rectangle.Inflate(e.Bounds, -7, 0), (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : SettingsPalette.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
        return combo;
    }
    private static TableLayoutPanel Stack() => new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, Margin = Padding.Empty, ColumnStyles = { new(SizeType.Percent, 100) } };
    private static void Add(TableLayoutPanel stack, Control control)
    {
        var row = stack.RowCount++; stack.RowStyles.Add(new(SizeType.AutoSize)); stack.Controls.Add(control, 0, row);
    }
    private static Panel CreatePage(string name)
    {
        var panel = new Panel { Name = name, Dock = DockStyle.Fill, AutoScroll = true, Visible = false, TabStop = false };
        var stack = Stack(); stack.Padding = new(0, 0, 12, 10); panel.Controls.Add(stack); return panel;
    }
    private SettingsCard Card(int page, string chinese, string english)
    {
        var card = new SettingsCard(); var title = BoundLabel(chinese, english, 11.5f, true); title.Margin = new(0, 0, 0, 6);
        Add(card, title); Add((TableLayoutPanel)_pages[page].Controls[0], card); return card;
    }
    private Control Field(SettingsCard card, string chinese, string english, Control input, string hintChinese = "", string hintEnglish = "")
    {
        var field = Stack(); field.Margin = new(0, 10, 0, 0);
        var label = BoundLabel(chinese, english); label.Margin = new(0, 0, 0, 7); Add(field, label);
        input.AccessibleName = T(chinese, english);
        Control host = input is TextBox text ? new SettingsTextFrame(text) : input;
        host.Margin = Padding.Empty; Add(field, host);
        if (hintChinese.Length > 0)
        {
            var hint = BoundLabel(hintChinese, hintEnglish); hint.ForeColor = SettingsPalette.Muted; hint.Margin = new(0, 7, 0, 0); Add(field, hint);
        }
        Add(card, field); return field;
    }
    private void BuildLanguagePage()
    {
        var languages = Card(0, "语言选择", "Language choices");
        Field(languages, "输入 / 视频语言", "Input / video language", _source);
        _customField = Field(languages, "自定义语言代码", "Custom language codes", _custom,
            "用逗号分隔，例如 zh,en；留空可自动识别。", "Separate codes with commas, such as zh,en. Leave blank to detect automatically.");
        Field(languages, "翻译目标语言", "Translate into", _target);
        var translation = Card(0, "自动翻译", "Automatic translation");
        Bind(_translate, "翻译完整语段", "Translate completed segments"); _translate.ForeColor = SettingsPalette.Ink; _translate.BackColor = SettingsPalette.Surface;
        _translate.Margin = new(0, 8, 0, 0); Add(translation, _translate);
        var hint = BoundLabel("原文始终保留，译文使用上方所选语言。", "Originals are preserved. Translations use the language selected above.");
        hint.ForeColor = SettingsPalette.Muted; hint.Margin = new(0, 8, 0, 0); Add(translation, hint);
    }
    private void BuildAudioPage()
    {
        var audio = Card(1, "电脑播放声音", "Computer audio");
        Field(audio, "播放设备", "Output device", _device,
            "默认选项会跟随 Windows 播放设备。不会采集麦克风。", "The default option follows your Windows audio output. Your microphone is not captured.");
        var recognition = Card(1, "语音识别", "Speech recognition");
        Field(recognition, "语音识别模型", "Transcription model", _transcribe);
        var notice = BoundLabel("点击“开始”后才会采集声音。所选设备上播放的其他应用声音也会包含在内。",
            "Audio capture begins only after Start. Sound from other apps using the selected output is included.");
        notice.ForeColor = SettingsPalette.Muted; notice.Margin = new(2, 0, 14, 12); Add((TableLayoutPanel)_pages[1].Controls[0], notice);
    }
    private void BuildConnectionPage()
    {
        var display = Card(2, "界面", "Interface"); Field(display, "界面语言", "Interface language", _uiLanguage);
        var connection = Card(2, "OpenAI 连接", "OpenAI connection");
        Field(connection, "API 密钥", "API key", _key,
            _hasOwnKey ? "密钥已保存。留空可保留，由当前 Windows 用户加密。" : "填写自己的 OpenAI API Key，由当前 Windows 用户加密保存。",
            _hasOwnKey ? "A key is saved. Leave blank to keep it. Encrypted for your Windows account." : "Enter your own OpenAI API key. It is encrypted for your Windows account.");
        Field(connection, "翻译模型", "Translation model", _model);
        var notice = BoundLabel("点击“开始”或手动翻译后才会调用付费 API。原文与译文自动加密保存在本机。",
            "Paid API calls begin only after Start or a manual translation. Originals and translations are encrypted locally.");
        notice.ForeColor = SettingsPalette.Muted; notice.Margin = new(2, 0, 14, 12); Add((TableLayoutPanel)_pages[2].Controls[0], notice);
    }
    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SettingsPalette.Surface, Padding = new(24, 12, 24, 12), ColumnCount = 2, RowCount = 2, TabIndex = 2 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.RowStyles.Add(new(SizeType.AutoSize)); footer.RowStyles.Add(new(SizeType.AutoSize));
        footer.Paint += (_, e) => { using var pen = new Pen(SettingsPalette.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        footer.Controls.Add(_error, 0, 0); footer.SetColumnSpan(_error, 2);
        footer.Resize += (_, _) => _error.MaximumSize = new(Math.Max(120, footer.ClientSize.Width - footer.Padding.Horizontal), 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        _cancel = ActionButton("settingsCancel", false); Bind(_cancel, "取消", "Cancel"); _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Click += (_, _) => Close();
        _save = ActionButton("settingsSave", true); Bind(_save, "保存设置", "Save changes"); _save.Click += (_, _) => SaveDraft();
        actions.Controls.Add(_cancel); actions.Controls.Add(_save); footer.Controls.Add(actions, 1, 1);
        AcceptButton = _save; CancelButton = _cancel; return footer;
    }
    private static Button ActionButton(string name, bool primary)
    {
        var button = new Button { Name = name, AutoSize = true, MinimumSize = new(108, 42), Padding = new(14, 4, 14, 4), Margin = new(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? SettingsPalette.Accent : SettingsPalette.Surface,
            ForeColor = primary ? Color.White : SettingsPalette.Ink, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = primary ? SettingsPalette.Accent : SettingsPalette.Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(19, 75, 62) : SettingsPalette.Canvas;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(13, 61, 49) : SettingsPalette.Selected;
        return button;
    }
    private void LoadChoices()
    {
        if (_source.SelectedItem is Choice source) _sourceCode = source.Code;
        if (_target.SelectedItem is Choice target) _targetCode = target.Code;
        if (_device.SelectedItem is AudioDeviceInfo device) _selectedDevice = device.Id;
        _source.Items.Clear(); _source.Items.AddRange(SourceOptions.Select(x => new Choice(x.Code, T(x.Chinese, x.English))).ToArray());
        _source.SelectedIndex = Array.FindIndex(SourceOptions, x => x.Code == _sourceCode);
        _target.Items.Clear(); _target.Items.AddRange(TranslationLanguages.Options.Select(x => new Choice(x.Code, T(x.ChineseName, x.EnglishName))).ToArray());
        _target.SelectedItem = _target.Items.Cast<Choice>().First(x => string.Equals(x.Code, _targetCode, StringComparison.OrdinalIgnoreCase));
        var devices = new List<AudioDeviceInfo> { new("", T("跟随 Windows 默认输出", "Follow the Windows default output")) }; devices.AddRange(_devices);
        if (!devices.Any(d => d.Id == _selectedDevice)) devices.Add(new(_selectedDevice, T("已断开的设备（保留选择）", "Disconnected device (keep selection)")));
        _device.DisplayMember = nameof(AudioDeviceInfo.Name); _device.DataSource = devices; _device.SelectedItem = devices.Single(d => d.Id == _selectedDevice);
    }
    private void ApplyLanguage()
    {
        _localizing = true;
        try
        {
            Text = T("听力字幕 · 设置", "Listening Subtitles · Settings");
            foreach (var entry in _localized) entry.Control.Text = T(entry.Chinese, entry.English);
            _navigation.AccessibleName = T("设置分类", "Settings categories");
            _navigation.Items.Clear();
            _navigation.Items.AddRange(new[]
            {
                new SettingsNavigationItem(T("语言与翻译", "Languages")),
                new SettingsNavigationItem(T("声音识别", "Audio")),
                new SettingsNavigationItem(T("连接与界面", "Connection"))
            });
            _navigation.SelectedIndex = _selectedPage; LoadChoices(); UpdateCustomField(); SelectPage(_selectedPage); ShowError();
            _key.AccessibleName = T("API 密钥", "API key"); _model.AccessibleName = T("翻译模型", "Translation model");
            _uiLanguage.AccessibleName = T("界面语言", "Interface language"); _source.AccessibleName = T("输入语言", "Input language");
            _target.AccessibleName = T("翻译目标语言", "Translate into"); _custom.AccessibleName = T("自定义语言代码", "Custom language codes");
            _device.AccessibleName = T("播放设备", "Output device"); _transcribe.AccessibleName = T("语音识别模型", "Transcription model");
        }
        finally { _localizing = false; }
        ResizeWrappedText();
    }
    private void UpdateCustomField()
    {
        var custom = (_source.SelectedItem as Choice)?.Code == "custom";
        _customField.Visible = custom; _custom.Enabled = custom;
    }
    private void SelectPage(int page)
    {
        _selectedPage = Math.Clamp(page, 0, 2);
        _heading.Text = _selectedPage switch { 0 => T("语言与翻译", "Languages & translation"), 1 => T("声音识别", "Audio & recognition"), _ => T("连接与界面", "Connection & interface") };
        _description.Text = _selectedPage switch
        {
            0 => T("选择视频使用的语言，以及希望看到的译文。", "Choose the language you hear and the translation you want."),
            1 => T("选择电脑声音的来源和识别模型。", "Choose your computer audio output and transcription model."),
            _ => T("管理界面语言与 OpenAI 连接。", "Manage the interface language and your OpenAI connection.")
        };
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _selectedPage;
        _pages[_selectedPage].BringToFront(); ResizeWrappedText();
    }
    private void ResizeWrappedText()
    {
        if (_pageHost.Width <= 0) return;
        _heading.MaximumSize = new(_pageHost.Width, 0); _description.MaximumSize = new(_pageHost.Width, 0);
        foreach (var page in _pages)
        {
            if (page is null) continue;
            var width = Math.Max(160, page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 14);
            foreach (var label in Descendants(page).OfType<Label>())
                label.MaximumSize = new(Math.Max(120, width - (label.Parent is SettingsCard ? 40 : 42)), 0);
            foreach (var check in Descendants(page).OfType<CheckBox>()) check.MaximumSize = new(Math.Max(120, width - 40), 0);
        }
    }
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private void SaveDraft()
    {
        var previousLanguage = AppText.Language; _invalidControl = null;
        try
        {
            AppText.Language = ((Choice)_uiLanguage.SelectedItem!).Code;
            var source = ((Choice)_source.SelectedItem!).Code;
            var candidate = _current with
            {
                InterfaceLanguage = AppText.Language, OutputDeviceId = ((AudioDeviceInfo)_device.SelectedItem!).Id,
                TranscriptionModel = _transcribe.Text, TranslationModel = _model.Text.Trim(),
                Languages = source == "custom" ? _custom.Text.Trim() : source,
                TargetLanguage = ((Choice)_target.SelectedItem!).Code, AutoTranslate = _translate.Checked
            };
            // Validate per field to put focus on the first actionable error.
            _invalidControl = _custom; (_current with { Languages = candidate.Languages }).Validate();
            _invalidControl = _model; (_current with { TranslationModel = candidate.TranslationModel }).Validate();
            _invalidControl = _device; candidate.Validate();
            _invalidControl = _key;
            if (NewKey.Length > 0) SecretStore.ValidateKey(NewKey);
            if (!_hasOwnKey && NewKey.Length == 0) throw new ArgumentException(T("请填写 OpenAI API Key。", "Enter your OpenAI API key."));
            Result = candidate; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message; ShowError();
            var page = _invalidControl == _custom ? 0 : _invalidControl == _device ? 1 : 2;
            _navigation.SelectedIndex = page;
            if (_invalidControl is { } input) { _pages[page].ScrollControlIntoView(input); input.Focus(); }
        }
        finally { AppText.Language = previousLanguage; }
    }
    private void ShowError()
    {
        _error.Text = _errorMessage.Length == 0 ? "" : T("请检查：", "Please check: ") + _errorMessage;
        _error.Visible = _errorMessage.Length > 0;
        _error.ForeColor = SettingsPalette.Error; _error.AccessibleName = _error.Text;
    }
    private void FitWorkingArea()
    {
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        var desiredMargin = Math.Max(8, (int)Math.Round(12 * DeviceDpi / 96f));
        var margin = Math.Max(0, Math.Min(desiredMargin, Math.Min((area.Width - 1) / 2, (area.Height - 1) / 2)));
        var available = new Size(Math.Max(1, area.Width - margin * 2), Math.Max(1, area.Height - margin * 2));
        MinimumSize = new(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
        Size = new(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
        var ownerBounds = Owner?.Bounds ?? area;
        Location = new(Math.Clamp(ownerBounds.Left + (ownerBounds.Width - Width) / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin)),
            Math.Clamp(ownerBounds.Top + (ownerBounds.Height - Height) / 2, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - Height - margin)));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); if (disposing) { foreach (var font in _fonts) font.Dispose(); _fonts.Clear(); _brandImage.Dispose(); }
    }
    private sealed record Choice(string Code, string Label) { public override string ToString() => Label; }
}
