using System.Globalization;
using System.Text;

namespace ListeningSubtitles.Core;

public enum CaptionLanguage { Bilingual, Original, Translation }
public enum CaptionFormat { Srt, Vtt }

public static class CaptionExporter
{
    // Segment times come from local audio capture, never from translation completion.
    public static string Export(CaptionRecord record, CaptionFormat format, CaptionLanguage language, TimeSpan offset, bool startAtFirstCaption = true,
        string? targetLanguage = null)
    {
        var output = new StringBuilder(format == CaptionFormat.Vtt ? "WEBVTT\n\n" : "");
        var rows = record.Captions.Where(c => c.Final).OrderBy(c => c.Time).ThenBy(c => c.Sequence).ToArray();
        var origin = CaptionTimeline.Origin(record, startAtFirstCaption);
        var index = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var translation = targetLanguage is null || string.Equals(row.TranslationLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase)
                ? row.Translation : "";
            var text = language switch
            {
                CaptionLanguage.Original => row.Original,
                CaptionLanguage.Translation => translation,
                _ => row.Original + "\n" + translation
            };
            text = Clean(text);
            if (text.Length == 0) continue;
            var start = (row.Time - origin + offset).TotalMilliseconds;
            var end = (row.EndTime is { } timedEnd ? timedEnd - origin + offset :
                row.Time - origin + offset + TimeSpan.FromSeconds(5)).TotalMilliseconds;
            if (i + 1 < rows.Length) end = Math.Min(end, (rows[i + 1].Time - origin + offset).TotalMilliseconds);
            if (end <= 0) continue;
            var from = (long)Math.Max(0, Math.Floor(start));
            var to = Math.Max(from + 1, (long)Math.Ceiling(end));
            output.Append(++index).Append('\n')
                .Append(Timestamp(from, format)).Append(" --> ").Append(Timestamp(to, format)).Append('\n')
                .Append(text).Append("\n\n");
        }
        if (index == 0) throw new InvalidOperationException(AppText.T("没有可导出的完整字幕；请等待完整句，或检查语言与时间偏移。", "There are no complete subtitles to export. Wait for a complete segment, or check the language and time offset."));
        return output.ToString();
    }
    public static void Write(string path, string content)
        => StorageFiles.WriteAtomic(path, new UTF8Encoding(false).GetBytes(content));
    private static string Timestamp(long ms, CaptionFormat format) => string.Create(CultureInfo.InvariantCulture,
        $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{(format == CaptionFormat.Srt ? ',' : '.')}{ms % 1000:000}");
    private static string Clean(string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Select(line => new string(line.Where(c => !char.IsControl(c) || c == '\t').ToArray()).Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
