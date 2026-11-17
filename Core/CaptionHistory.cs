namespace ListeningSubtitles.Core;

public sealed record Caption(string Id, long Session, long Sequence, DateTimeOffset Time, string Original, bool Final,
    string Chinese = "", string TranslationStatus = "", DateTimeOffset? EndTime = null);

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
            incoming = incoming with { Chinese = old.Chinese, TranslationStatus = old.TranslationStatus };
        if (old == incoming) return;
        _entries[incoming.Id] = incoming;
        Changed?.Invoke();
    }
    public void Translate(TranscriptTranslationUpdate update)
    {
        if (!_entries.TryGetValue(update.Id, out var caption) || !caption.Final || caption.Original != update.OriginalText) return;
        var changed = caption with
        {
            Chinese = update.Translation.Length > 0 ? update.Translation : caption.Chinese,
            TranslationStatus = caption.Chinese.Length > 0 && update.Translation.Length == 0 ? "completed" : update.Status
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
    public string Export(bool startAtFirstCaption = true, TimeSpan offset = default)
    {
        var origin = CaptionTimeline.Origin(Snapshot(), startAtFirstCaption);
        return string.Join(Environment.NewLine + Environment.NewLine, Entries.Select(c =>
            $"[{CaptionTimeline.Display(c.Time, origin, offset)}] {(c.Final ? "" : "[未完成] ")}{c.Original}\n{c.Chinese}"));
    }
}
