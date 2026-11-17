using System.Text.Json.Serialization;

namespace ListeningSubtitles.Core;

public sealed record Caption(string Id, long Session, long Sequence, DateTimeOffset Time, string Original, bool Final,
    [property: JsonPropertyName("Chinese")] string Translation = "", string TranslationStatus = "", DateTimeOffset? EndTime = null,
    string TranslationLanguage = "zh-Hans", string SourceLanguages = "");

public sealed record CaptionRecord(int Version, Guid Id, DateTimeOffset StartedAt, Caption[] Captions);

// UI-thread owned: late translations update their own row, never select an older subtitle.
public sealed class CaptionHistory
{
    private readonly Dictionary<string, Caption> _entries = new(StringComparer.Ordinal);
    public Guid Id { get; private set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;
    public event Action? Changed;
    public IReadOnlyList<Caption> Entries => _entries.Values.OrderBy(x => x.Session).ThenBy(x => x.Sequence).ToArray();
    public Caption? Latest => Entries.LastOrDefault();
    public void Upsert(Caption incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming.Original)) return;
        if (_entries.TryGetValue(incoming.Id, out var old) && old.Final && !incoming.Final) return;
        // A partial has no translation state to preserve when promoted to a final.
        if (old is { Final: true } && old.Original == incoming.Original)
            incoming = incoming with { Translation = old.Translation, TranslationStatus = old.TranslationStatus,
                TranslationLanguage = old.TranslationLanguage, SourceLanguages = old.SourceLanguages };
        if (old == incoming) return;
        _entries[incoming.Id] = incoming;
        Changed?.Invoke();
    }
    public void Translate(TranscriptTranslationUpdate update)
    {
        if (!_entries.TryGetValue(update.Id, out var caption) || !caption.Final || caption.Original != update.OriginalText) return;
        var completed = update.Status == "completed" && !string.IsNullOrWhiteSpace(update.Translation) && TranslationLanguages.IsSupported(update.TargetLanguage);
        var changed = caption with
        {
            Translation = completed ? update.Translation : caption.Translation,
            TranslationLanguage = completed ? TranslationLanguages.Find(update.TargetLanguage)!.Code : caption.TranslationLanguage,
            TranslationStatus = caption.Translation.Length > 0 && !completed ? "completed" : update.Status
        };
        if (caption == changed) return;
        _entries[update.Id] = changed; Changed?.Invoke();
    }
    public void MarkStopped()
    {
        var changed = false;
        foreach (var entry in _entries.Values.ToArray())
            if (entry.Final && entry.TranslationStatus is "queued" or "translating")
            { _entries[entry.Id] = entry with { TranslationStatus = "cancelled" }; changed = true; }
        if (changed) Changed?.Invoke();
    }
    public void SetStartedAt(DateTimeOffset value) { StartedAt = value; Changed?.Invoke(); }
    public void Clear()
    {
        _entries.Clear(); Id = Guid.NewGuid(); StartedAt = DateTimeOffset.UtcNow; Changed?.Invoke();
    }
    public CaptionRecord Snapshot() => new(1, Id, StartedAt, Entries.ToArray());
    public void Load(CaptionRecord record)
    {
        _entries.Clear(); Id = record.Id; StartedAt = record.StartedAt;
        foreach (var entry in record.Captions) _entries.Add(entry.Id, entry);
        MarkStopped(); Changed?.Invoke();
    }
    public string Export(bool startAtFirstCaption = true, TimeSpan offset = default, string? targetLanguage = null,
        CaptionLanguage language = CaptionLanguage.Bilingual)
    {
        var origin = CaptionTimeline.Origin(Snapshot(), startAtFirstCaption);
        var rows = new List<string>();
        foreach (var caption in Entries)
        {
            var translation = targetLanguage is null || string.Equals(caption.TranslationLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase)
                ? caption.Translation : "";
            if (language == CaptionLanguage.Translation && string.IsNullOrWhiteSpace(translation)) continue;
            var text = language switch
            {
                CaptionLanguage.Original => caption.Original,
                CaptionLanguage.Translation => translation,
                _ => caption.Original + "\n" + translation
            };
            rows.Add($"[{CaptionTimeline.Display(caption.Time, origin, offset)}] {(caption.Final ? "" : AppText.T("[未完成] ", "[Incomplete] "))}{text}");
        }
        if (language == CaptionLanguage.Translation && rows.Count == 0)
            throw new InvalidOperationException(AppText.T("没有可导出的目标语言译文，请先补译 / 重译。", "There are no translations in the selected language to export. Fill / retranslate first."));
        return string.Join(Environment.NewLine + Environment.NewLine, rows);
    }
}
