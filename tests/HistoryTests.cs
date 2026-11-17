using System.Reflection;
using System.Text;
using ListeningSubtitles.Core;
using ListeningSubtitles.UI;

internal static class HistoryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Temporary() => Path.Combine(Path.GetTempPath(), "Listening-history-test-" + Guid.NewGuid().ToString("N"));
    public static Task Persistence()
    {
        var directory = Temporary();
        try
        {
            var store = new CaptionStore(directory);
            Check(store.List().Count == 0 && !Directory.Exists(directory), "read created storage");
            var history = new CaptionHistory();
            var start = DateTimeOffset.UtcNow.AddHours(-1); history.SetStartedAt(start);
            for (int i = 0; i < 605; i++)
                history.Upsert(new("id-" + i, 1, i, start.AddSeconds(i * 5), "An English sentence.", true,
                    TranslationStatus: "queued", EndTime: start.AddSeconds(i * 5 + 4)));
            var path = store.Save(history);
            var id = history.Id;
            history.Translate(new("id-0", "An English sentence.", "最早的一段中文也要保留。", "completed"));
            store.Save(history);
            Check(store.List().Count == 1, "late translation created duplicate archive");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("English sentence"), "history stored plaintext");
            var restored = new CaptionHistory(); restored.Load(new CaptionStore(directory).Read(store.List().Single()));
            Check(restored.Id == id && restored.Entries.Count == 605, "restart lost records or identity");
            Check(restored.Entries[0].Translation == "最早的一段中文也要保留。", "restart lost Chinese");
            Check(restored.Entries[1].TranslationStatus == "cancelled", "restored stale translation marked running");
            Check(restored.Entries[0].EndTime == start.AddSeconds(4), "timing lost on disk");
            history.Clear(); history.Upsert(new("id-0", 1, 1, DateTimeOffset.UtcNow, "New session.", true));
            store.Save(history);
            Check(store.List().Count == 2 && history.Id != id, "new recording overwrote previous session");
            // Damaged storage must fail visibly without overwriting the original file.
            var damaged = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(path, damaged);
            var rejected = false;
            try { store.Read(store.List().Single(x => x.FileName == Path.GetFileName(path))); } catch { rejected = true; }
            Check(rejected && File.ReadAllBytes(path).SequenceEqual(damaged), "corrupt file silently replaced");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    public static Task SubtitleFiles(string? destination = null)
    {
        var history = new CaptionHistory();
        var start = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero); history.SetStartedAt(start);
        history.Upsert(new("first", 1, 1, start.AddSeconds(1.25), "First <tag> & text\n\nsecond line --> here.", true, "第一句话。", "completed", start.AddSeconds(3.5)));
        history.Upsert(new("second", 1, 2, start.AddSeconds(4), "Second sentence.", true, "第二句话。", "completed", start.AddSeconds(6)));
        history.Upsert(new("partial", 1, 3, start.AddSeconds(7), "Must not be exported.", false));
        var srt = CaptionExporter.Export(history.Snapshot(), CaptionFormat.Srt, CaptionLanguage.Bilingual, TimeSpan.Zero);
        Check(srt.StartsWith("1\n00:00:00,000 --> 00:00:02,250\n"), "SRT must start at zero and preserve cue duration");
        Check(srt.Contains("第一句话。\n\n2\n") && !srt.Contains("Must not"), "Chinese/partial export incorrect");
        Check(!srt.Contains("<tag>") && srt.Contains("&lt;tag&gt; &amp;"), "text interpreted as cue markup");
        var vtt = CaptionExporter.Export(history.Snapshot(), CaptionFormat.Vtt, CaptionLanguage.Bilingual, TimeSpan.FromSeconds(60));
        Check(vtt.StartsWith("WEBVTT\n\n1\n00:01:00.000 --> 00:01:02.250\n") && vtt.Contains("第二句话。"), "VTT/offset invalid");
        var chinese = CaptionExporter.Export(history.Snapshot(), CaptionFormat.Srt, CaptionLanguage.Translation, TimeSpan.FromSeconds(-2));
        Check(chinese.Contains("00:00:00,000 --> 00:00:00,250") && !chinese.Contains("First") && chinese.Contains("第一"), "negative shift/Chinese-only invalid");
        var recording = CaptionExporter.Export(history.Snapshot(), CaptionFormat.Srt, CaptionLanguage.Bilingual, TimeSpan.Zero, false);
        Check(recording.StartsWith("1\n00:00:01,250 --> 00:00:03,500\n"), "recording origin option lost initial silence");
        Check(history.Export().StartsWith("[00:00:00.000]") && history.Export().Contains("[00:00:02.750]"), "TXT origin differs from captions");
        if (destination is not null)
        {
            CaptionExporter.Write(Path.Combine(Path.GetFullPath(destination), "sample.srt"), srt);
            CaptionExporter.Write(Path.Combine(Path.GetFullPath(destination), "sample.vtt"), vtt);
        }
        var directory = Temporary();
        try
        {
            CaptionExporter.Write(Path.Combine(directory, "captions.srt"), srt);
            Check(File.ReadAllText(Path.Combine(directory, "captions.srt"), new UTF8Encoding(false, true)) == srt, "UTF-8 file roundtrip failed");
            CaptionExporter.Write(Path.Combine(directory, "captions.srt"), vtt);
            Check(File.ReadAllText(Path.Combine(directory, "captions.srt")) == vtt, "atomic overwrite failed");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    public static Task RelativeTime()
    {
        var history = new CaptionHistory();
        var start = new DateTimeOffset(2026, 11, 17, 23, 59, 50, TimeSpan.FromHours(13));
        history.SetStartedAt(start);
        history.Upsert(new("first", 1, 1, start.AddSeconds(5), "First sentence.", true, "第一句。", "completed", start.AddSeconds(7)));
        history.Upsert(new("second", 1, 2, start.AddSeconds(20), "After midnight.", true, "跨过午夜。", "completed", start.AddSeconds(23)));
        var text = history.Export();
        Check(text.StartsWith("[00:00:00.000]") && text.Contains("[00:00:15.000]") && !text.Contains("23:59"), "wall clock leaked into relative TXT");
        Check(history.Export(false).StartsWith("[00:00:05.000]"), "recording-relative TXT lost silence");
        Check(history.Export(true, TimeSpan.FromSeconds(60)).StartsWith("[00:01:00.000]"), "TXT offset ignored");
        var snapshot = history.Snapshot();
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<CaptionRecord>(System.Text.Json.JsonSerializer.Serialize(snapshot))!;
        var reopened = new CaptionHistory(); reopened.Load(roundtrip);
        Check(reopened.Export() == text, "zero timeline changed after restore");
        var origin = CaptionTimeline.Origin(snapshot);
        Check(CaptionTimeline.Display(origin.AddHours(27).AddMilliseconds(3), origin) == "27:00:00.003", "long timeline wrapped at 24 hours");
        var utc = snapshot with { StartedAt = snapshot.StartedAt.ToUniversalTime(),
            Captions = snapshot.Captions.Select(c => c with { Time = c.Time.ToUniversalTime(), EndTime = c.EndTime?.ToUniversalTime() }).ToArray() };
        Check(CaptionExporter.Export(utc, CaptionFormat.Srt, CaptionLanguage.Bilingual, default) ==
            CaptionExporter.Export(snapshot, CaptionFormat.Srt, CaptionLanguage.Bilingual, default), "time zone changed captions");
        return Task.CompletedTask;
    }

    public static void Ui()
    {
        var directory = Temporary();
        try
        {
            var store = new CaptionStore(directory);
            var history = new CaptionHistory();
            history.Upsert(new("test", 1, 1, DateTimeOffset.Now, "A missing translation.", true, TranslationStatus: "cancelled"));
            store.Save(history);
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using (var form = new HistoryForm(history, store, () => (text, sourceLanguages, token) => { calls++; return result.Task; }))
            {
                form.Show(); Application.DoEvents();
                Check(calls == 0, "opening history invoked API");
                var list = form.Controls.OfType<ListBox>().Single();
                Check(list.Items[0]!.ToString()!.StartsWith("[00:00:00.000]"), "history still displays wall clock");
                var task = (Task)typeof(HistoryForm).GetMethod("FillTranslationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null)!;
                Check(calls == 1, "explicit retry did not start");
                result.SetResult("补译完成并保存。");
                Pump(() => task.IsCompleted);
                task.GetAwaiter().GetResult();
                Check(history.Latest!.Translation == "补译完成并保存。", "manual retry lost Chinese");
                Check(form.Controls.OfType<RichTextBox>().Single().Text.Contains("补译完成"), "manual translation did not update open history");
                Check(store.Read(store.List().Single()).Captions.Single().Translation == "补译完成并保存。", "manual translation not saved");
                form.Close();
            }
            // Real form lifecycle, isolated data directory and no recording/API.
            var settings = new ListeningSettingsStore(directory);
            using (var form = new SubtitleForm(settings, new ListeningSettings()))
            {
                form.Show(); Application.DoEvents();
                var restored = (CaptionHistory)typeof(SubtitleForm).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                Check(restored.Latest!.Translation == "补译完成并保存。", "app startup did not restore history");
                restored.Upsert(new("new", 1, 2, DateTimeOffset.Now, "Auto save me.", true, "自动保存。", "completed"));
                Pump(() => store.Read(store.List().Single()).Captions.Length == 2);
                form.Close(); Pump(() => form.IsDisposed);
            }
            using (var form = new SubtitleForm(settings, new ListeningSettings()))
            {
                form.Show(); Application.DoEvents();
                var restored = (CaptionHistory)typeof(SubtitleForm).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                Check(restored.Latest!.Translation == "自动保存。", "auto save did not survive reopen");
                form.Close(); Pump(() => form.IsDisposed);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), "UI operation timed out");
    }
}
