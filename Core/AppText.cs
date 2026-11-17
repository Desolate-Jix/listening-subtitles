namespace ListeningSubtitles.Core;

/// <summary>Application chrome language; independent of speech and translation languages.</summary>
public static class AppText
{
    private static string _language = "zh-CN";

    public static string Language
    {
        get => Volatile.Read(ref _language);
        set
        {
            if (value is not ("zh-CN" or "en")) throw new ArgumentException("Unsupported interface language.", nameof(value));
            Volatile.Write(ref _language, value);
        }
    }

    public static string T(string chinese, string english) => Language == "en" ? english : chinese;
}
