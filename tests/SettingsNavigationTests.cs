using System.Drawing.Imaging;
using System.Reflection;
using ListeningSubtitles.Core;
using ListeningSubtitles.UI;

internal static class SettingsNavigationTests
{
    public static void Ui(string? destination)
    {
        var callerLanguage = AppText.Language;
        var initial = new ListeningSettings { LyricsLocked = true, LockedLyricCenter = new Point(500, 500) };
        try
        {
            using (var saved = new SettingsForm(initial, false))
            {
                saved.Show(); Application.DoEvents();
                Page(saved, 0);
                var navigation = Find<ListBox>(saved, "settingsNavigation");
                navigation.Focus(); DialogKey(saved, Keys.Tab);
                var focused = Descendants(saved).FirstOrDefault(control => control.Focused);
                Check(focused is { Visible: true, Enabled: true } && focused != navigation, "Tab did not leave navigation for a visible settings control");
                Select(Find<ComboBox>(saved, "sourceLanguage"), "custom");
                Find<TextBox>(saved, "customSourceLanguages").Text = "zh,ja";
                Select(Find<ComboBox>(saved, "targetLanguage"), "en");
                Find<CheckBox>(saved, "autoTranslate").Checked = false;
                Snapshot(saved, destination, "settings-languages.png");
                Page(saved, 1);
                Find<ComboBox>(saved, "transcriptionModel").SelectedItem = "gpt-transcribe";
                Find<ComboBox>(saved, "outputDevice").SelectedIndex = 0;
                Page(saved, 2);
                Find<TextBox>(saved, "apiKey").Text = "sk-synthetic-settings-only";
                Find<TextBox>(saved, "translationModel").Text = "gpt-6-luna-settings-test";
                Select(Find<ComboBox>(saved, "interfaceLanguage"), "en");
                Check(AppText.Language == callerLanguage, "editing settings changed the caller's live interface language");
                Page(saved, 0);
                Check(Find<TextBox>(saved, "customSourceLanguages").Text == "zh,ja", "switching pages lost custom source-language draft");
                Page(saved, 1);
                Check(Find<ComboBox>(saved, "transcriptionModel").Text == "gpt-transcribe", "switching pages lost the recognition-model draft");
                Page(saved, 2);
                Check(saved.NewKey == "sk-synthetic-settings-only", "switching pages lost the unsaved API-key draft");
                saved.Size = new(720, 500); saved.PerformLayout(); Application.DoEvents();
                AssertFooter(saved); Snapshot(saved, destination, "settings-connection-compact.png");
                Find<TextBox>(saved, "translationModel").Focus(); DialogKey(saved, Keys.Return);
                var result = saved.Result ?? throw new Exception("Enter did not save valid settings across all pages");
                Check(result.Languages == "zh,ja" && result.TargetLanguage == "en" && result.InterfaceLanguage == "en" &&
                    result.TranscriptionModel == "gpt-transcribe" && result.OutputDeviceId == "" && !result.AutoTranslate &&
                    result.TranslationModel == "gpt-6-luna-settings-test" && result.LyricsLocked && result.LockedLyricCenter == initial.LockedLyricCenter,
                    "saving settings lost page drafts or unrelated lyric preferences");
            }
            using (var cancelled = new SettingsForm(initial, true))
            {
                cancelled.Show(); Application.DoEvents();
                Page(cancelled, 2); Select(Find<ComboBox>(cancelled, "interfaceLanguage"), "en");
                Find<TextBox>(cancelled, "translationModel").Text = "discarded-model";
                Page(cancelled, 0); Select(Find<ComboBox>(cancelled, "sourceLanguage"), "ja");
                DialogKey(cancelled, Keys.Escape);
                Check(cancelled.Result is null && initial.Languages == "en" && initial.TranslationModel == "gpt-6-luna" && AppText.Language == callerLanguage,
                    "Escape applied or persisted the cancelled page drafts");
            }
            Error(initial, false, 2, "apiKey", null, destination);
            Error(initial, true, 0, "customSourceLanguages", form =>
            {
                Page(form, 0); Select(Find<ComboBox>(form, "sourceLanguage"), "custom");
                Find<TextBox>(form, "customSourceLanguages").Text = "not valid!"; Page(form, 2);
            }, null);
            Error(initial with { InterfaceLanguage = "en" }, true, 2, "translationModel", form =>
            {
                Page(form, 2); Find<TextBox>(form, "translationModel").Text = "invalid model"; Page(form, 1);
            }, null);
        }
        finally { AppText.Language = callerLanguage; }
    }

    private static void Error(ListeningSettings initial, bool hasKey, int expectedPage, string focusName, Action<SettingsForm>? prepare, string? destination)
    {
        using var form = new SettingsForm(initial, hasKey);
        form.Show(); Application.DoEvents(); prepare?.Invoke(form);
        DialogKey(form, Keys.Return); Application.DoEvents();
        var input = Find<TextBox>(form, focusName);
        var error = Find<Label>(form, "settingsError");
        Check(form.Result is null && Find<ListBox>(form, "settingsNavigation").SelectedIndex == expectedPage && input.Visible && input.Focused,
            "isolated validation error did not select and focus its settings page: " + focusName);
        Check(error.Visible && error.Text.Length > 0, "settings validation error is hidden or empty");
        AssertFooter(form);
        if (destination is not null) Snapshot(form, destination, "settings-key-error.png");
        DialogKey(form, Keys.Escape); Check(form.Result is null, "cancelling an invalid form saved settings");
    }
    private static void Page(SettingsForm form, int selected)
    {
        Find<ListBox>(form, "settingsNavigation").SelectedIndex = selected; form.PerformLayout();
        var names = new[] { "settingsLanguagesPage", "settingsAudioPage", "settingsConnectionPage" };
        for (int i = 0; i < names.Length; i++) Check(Find<Panel>(form, names[i]).Visible == (i == selected), "settings navigation displayed the wrong page");
        AssertFooter(form);
    }
    private static void AssertFooter(SettingsForm form)
    {
        var save = Find<Button>(form, "settingsSave"); var cancel = Find<Button>(form, "settingsCancel");
        var error = Find<Label>(form, "settingsError");
        Rectangle Bounds(Control control) => form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        Check(save.Visible && cancel.Visible && form.ClientRectangle.Contains(Bounds(save)) && form.ClientRectangle.Contains(Bounds(cancel)),
            "fixed settings save/cancel actions are clipped or unavailable");
        if (error.Visible)
            Check(form.ClientRectangle.Contains(Bounds(error)) && !Bounds(error).IntersectsWith(Bounds(save)), "fixed validation message is clipped or overlaps Save");
    }
    private static void Snapshot(SettingsForm form, string? destination, string name)
    {
        if (destination is null) return;
        Directory.CreateDirectory(destination);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(destination, name), ImageFormat.Png);
    }
    private static void Select(ComboBox combo, string code) => combo.SelectedItem = combo.Items.Cast<object>()
        .Single(item => (string)item.GetType().GetProperty("Code")!.GetValue(item)! == code);
    private static void DialogKey(Form form, Keys key) => typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(form, new object[] { key });
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static T Find<T>(Control root, string name) where T : Control => Descendants(root).OfType<T>().Single(control => control.Name == name);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
