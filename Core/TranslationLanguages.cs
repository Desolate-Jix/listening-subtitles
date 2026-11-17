namespace ListeningSubtitles.Core;

public sealed record TranslationLanguageOption(string Code, string ChineseName, string EnglishName)
{
    public override string ToString() => AppText.T(ChineseName, EnglishName);
}

public static class TranslationLanguages
{
    public static IReadOnlyList<TranslationLanguageOption> Options { get; } = Array.AsReadOnly(new[]
    {
        new TranslationLanguageOption("zh-Hans", "简体中文", "Simplified Chinese"),
        new("zh-Hant", "繁体中文", "Traditional Chinese"),
        new("en", "英语", "English"),
        new("ja", "日语", "Japanese"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("ru", "俄语", "Russian"),
        new("ar", "阿拉伯语", "Arabic"),
        new("hi", "印地语", "Hindi"),
        new("it", "意大利语", "Italian")
    });

    public static bool IsSupported(string? code) => Find(code) is not null;
    public static string DisplayName(string code) => Find(code)?.ToString() ?? code;
    internal static TranslationLanguageOption? Find(string? code) =>
        Options.FirstOrDefault(option => string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase));
}
