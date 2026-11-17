using System.Globalization;

namespace ListeningSubtitles.Core;

public static class CaptionTimeline
{
    public static DateTimeOffset Origin(CaptionRecord record, bool startAtFirstCaption = true)
    {
        if (!startAtFirstCaption || record.Captions.Length == 0) return record.StartedAt;
        var finals = record.Captions.Where(c => c.Final).ToArray();
        return (finals.Length > 0 ? finals : record.Captions).Min(c => c.Time);
    }

    public static string Display(DateTimeOffset time, DateTimeOffset origin, TimeSpan offset = default)
    {
        var ms = Math.Max(0, (time - origin + offset).Ticks / TimeSpan.TicksPerMillisecond);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }
}
