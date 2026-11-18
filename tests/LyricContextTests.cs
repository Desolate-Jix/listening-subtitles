using System.Reflection;
using ListeningSubtitles.Core;
using ListeningSubtitles.UI;

internal static class LyricContextTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Ui(string? destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Listening-context-test-" + Guid.NewGuid().ToString("N"));
        var previousLanguage = AppText.Language;
        try
        {
            var store = new ListeningSettingsStore(directory);
            using var form = new SubtitleForm(store, store.Load());
            form.Show(); Application.DoEvents();
            var history = Field<CaptionHistory>(form, "_history");
            var overlay = Field<LyricOverlay>(form, "_overlay");
            var show = Field<CheckBox>(form, "_show");
            var at = DateTimeOffset.UtcNow;
            Caption Cue(string id, long sequence, string text, long session = 1) =>
                new(id, session, sequence, at.AddSeconds(-sequence), text, true, TranslationStatus: "queued");
            void Render() { typeof(SubtitleForm).GetMethod("Render", Private)!.Invoke(form, null); Application.DoEvents(); }
            void Add(Caption caption) { history.Upsert(caption); Render(); }
            void Translate(string id, string translation, string language = "zh-Hans")
            {
                history.Translate(new(id, history.Entries.Single(row => row.Id == id).Original, translation, "completed", language)); Render();
            }
            void Current(string original, string translation = "")
            {
                Check(Field<string>(overlay, "_original") == original && Field<string>(overlay, "_translation") == translation,
                    "late/partial updates replaced the current final subtitle or bound a translation to the wrong cue");
            }
            void Previous(string expected)
            {
                var actual = Field<string>(overlay, "_previous");
                Check(expected.Length == 0 ? actual.Length == 0 : actual.Contains(expected, StringComparison.Ordinal) && actual != expected,
                    "previous context is missing, stale, unlabelled, or selected by translation arrival: " + actual);
            }

            var a = Cue("a", 10, "Listen for the rhythm.");
            var b = Cue("b", 20, "Follow the melody.");
            var c = Cue("c", 30, "The chorus is about to begin.");
            Add(a); Current(a.Original); Previous("");
            Add(b); Current(b.Original); Previous(a.Original);
            Add(c); Current(c.Original); Previous(b.Original);
            Translate("a", "听听这段旋律。"); Current(c.Original); Previous("听听这段旋律。");
            Translate("b", "跟上这段旋律。"); Current(c.Original); Previous("跟上这段旋律。");
            Translate("a", "更晚到达的第一句译文。"); Current(c.Original); Previous("跟上这段旋律。");
            Translate("c", "副歌即将开始。"); Current(c.Original, "副歌即将开始。"); Previous("跟上这段旋律。");
            show.Checked = false; Application.DoEvents(); Current(c.Original); Previous(b.Original);
            show.Checked = true; Application.DoEvents(); Current(c.Original, "副歌即将开始。"); Previous("跟上这段旋律。");
            // Use the bound context/current/translation, with a synthetic active indicator.
            overlay.SetContent(Field<string>(overlay, "_original"), Field<string>(overlay, "_translation"), "", "", true, false, 20,
                Field<string>(overlay, "_previous"));
            using (var preview = overlay.CaptureFrame()) LyricOverlayTests.AssertCompositing(preview, destination, "lyrics-context");
            Add(new("partial-d", 1, 40, at.AddHours(1), "A new phrase is still arriving", false));
            Current(c.Original, "副歌即将开始。"); Previous("跟上这段旋律。");
            Check(Field<string>(overlay, "_live") == "A new phrase is still arriving", "partial text did not remain separate from final/context rows");

            // Preserve a useful completed translation even when it trails many final cues.
            for (int sequence = 40; sequence <= 140; sequence += 10)
                Add(Cue("lag-" + sequence, sequence, "Pending phrase " + sequence + "."));
            Current("Pending phrase 140."); Previous("副歌即将开始。");
            Translate("a", "又一次迟到的旧译文。"); Previous("副歌即将开始。");
            Translate("lag-110", "更接近当前的已完成译文。"); Previous("更接近当前的已完成译文。");
            Translate("lag-40", "较旧但更晚返回的译文。"); Previous("更接近当前的已完成译文。");
            Translate("lag-130", "An earlier translation in another target.", "en"); Previous("更接近当前的已完成译文。");
            Current("Pending phrase 140.");
            Add(new("partial-next", 1, 150, at, "Still listening to the next phrase", false));
            Current("Pending phrase 140."); Previous("更接近当前的已完成译文。");
            show.Checked = false; Application.DoEvents();
            Current("Pending phrase 140."); Previous("Pending phrase 130.");
            show.Checked = true; Application.DoEvents(); Previous("更接近当前的已完成译文。");

            history.Clear(); Render(); Previous("");
            Check(Field<string>(overlay, "_live").Length == 0, "clearing a recording retained an old partial");
            Add(Cue("new-first", 1, "First cue after clearing.")); Current("First cue after clearing."); Previous("");
            Translate("new-first", "新记录的第一句译文。");
            Add(Cue("session-two", 1, "First cue of another speech session.", 2));
            Current("First cue of another speech session."); Previous("");
            Add(Cue("session-two-next", 2, "Second cue in this speech session.", 2));
            Current("Second cue in this speech session."); Previous("First cue of another speech session.");
            Translate("new-first", "前一场次的迟到译文。"); Previous("First cue of another speech session.");
            history.Clear(); Render(); Previous("");
            Add(new("partial-only", 3, 1, at, "Only a first partial", false)); Previous("");

            CompactRow(destination);
            var session = Field<LiveSession>(form, "_session");
            Check(!session.Active && Field<long>(session, "_generation") == 0, "context tests attempted a real audio/API session");
            form.Close(); Pump(() => form.IsDisposed);
        }
        finally
        {
            AppText.Language = previousLanguage;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void CompactRow(string? destination)
    {
        foreach (var font in new[] { 20, 36 })
        {
            LyricRenderer.Frame Render(string previous) => LyricRenderer.Render(new Size(1056, 170), 1,
                "The chorus is about to begin.", "副歌即将开始。", "A partial is still arriving.", "Translation failed; the original is preserved.", true, false, font, previous: previous);
            var first = Render("上一句：听听这段旋律。");
            var second = Render("上一句：沿着节拍继续听。");
            using (first.Image)
            using (second.Image)
            {
                LyricOverlayTests.AssertAlpha(first.Image, "compact context font " + font);
                LyricOverlayTests.AssertToolbarArea(first.Image, first.Toolbar, false);
                var bands = TextBands(first.Image, first.Toolbar.Bottom + 3, first.Image.Height);
                Check(bands.Count >= 4, "previous, current, translation and error status are not four separate compact text rows");
                int changed = 0, lastChangedRow = -1;
                for (int y = 0; y < first.Image.Height; y++)
                    for (int x = 0; x < first.Image.Width; x++)
                        if (first.Image.GetPixel(x, y) != second.Image.GetPixel(x, y)) { changed++; lastChangedRow = y; }
                Check(changed > 50 && lastChangedRow < bands[1].Top,
                    "the previous-context parameter is absent or painted into the current subtitle row");
                LyricOverlayTests.AssertCompositing(first.Image, destination, "lyrics-context-compact-font" + font);
            }
            var multiline = Render("上句：A\r\nB\rC\nD\u2028E\u2029F");
            var flattened = Render("上句：A B C D E F");
            using (multiline.Image)
            using (flattened.Image)
            {
                Check(SamePixels(multiline.Image, flattened.Image), "previous context line breaks created extra rows or lost later words");
            }
        }
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }

    private static List<(int Top, int Bottom)> TextBands(Bitmap image, int top, int bottom)
    {
        var rows = new List<int>();
        for (int y = top; y < bottom; y++)
        {
            var bright = 0;
            for (int x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.A > 96 && Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) > 100) bright++;
            }
            if (bright >= 4) rows.Add(y);
        }
        var bands = new List<(int Top, int Bottom)>();
        foreach (var row in rows)
        {
            if (bands.Count == 0 || row > bands[^1].Bottom + 3) bands.Add((row, row));
            else bands[^1] = (bands[^1].Top, row);
        }
        return bands;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(completed(), "context test application close timed out");
    }
}
