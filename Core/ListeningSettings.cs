using System.Text.Json;
using System.Text.RegularExpressions;

namespace ListeningSubtitles.Core;

public sealed record ListeningSettings
{
    public string OutputDeviceId { get; init; } = "";
    public string TranscriptionModel { get; init; } = "gpt-live-transcribe";
    public string Languages { get; init; } = "en";
    public string TranslationModel { get; init; } = "gpt-6-luna";
    public bool AutoTranslate { get; init; } = true;
    public bool ShowChinese { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
    public int FontSize { get; init; } = 20;

    public void Validate()
    {
        if (OutputDeviceId is null || OutputDeviceId.Length > 1024) throw new ArgumentException("输出设备无效。");
        if (TranscriptionModel is not ("gpt-live-transcribe" or "gpt-transcribe")) throw new ArgumentException("请选择支持的转写模型。");
        if (Languages is null || Languages.Length > 80 || !Regex.IsMatch(Languages, @"\A(?:[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,4})?)(?:,[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,4})?){0,7}\z|\A\z"))
            throw new ArgumentException("语言请填 en、zh,en 等代码，留空自动识别。");
        if (TranslationModel is null || !Regex.IsMatch(TranslationModel, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))
            throw new ArgumentException("翻译模型 ID 无效。");
        if (FontSize is < 12 or > 36) throw new ArgumentException("字号须为 12–36。");
    }
}

public sealed class ListeningSettingsStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ListeningSubtitles");
    public string DirectoryPath { get; }
    private string PathName => Path.Combine(DirectoryPath, "settings.json");
    public ListeningSettingsStore(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory);
    public ListeningSettings Load()
    {
        if (!File.Exists(PathName)) return new();
        try
        {
            if (new FileInfo(PathName).Length > 64 * 1024) throw new InvalidDataException();
            var settings = JsonSerializer.Deserialize<ListeningSettings>(File.ReadAllBytes(PathName)) ?? throw new InvalidDataException();
            settings.Validate(); return settings;
        }
        catch { throw new InvalidDataException("听力字幕设置无法读取，请恢复设置文件后再启动。原文件未被覆盖。"); }
    }
    public void Save(ListeningSettings settings)
    {
        settings.Validate();
        StorageFiles.WriteAtomic(PathName, JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class Credentials
{
    public static string Read(string directory) =>
        new SecretStore(directory).Read() ?? throw new InvalidOperationException("请先在设置中填写本应用的 OpenAI API Key。");
}
