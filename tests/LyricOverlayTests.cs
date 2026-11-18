using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ListeningSubtitles.Core;
using ListeningSubtitles.UI;

internal static class LyricOverlayTests
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Ui(string? destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Listening-lyrics-test-" + Guid.NewGuid().ToString("N"));
        var previousLanguage = AppText.Language;
        try
        {
            var store = new ListeningSettingsStore(directory);
            using var form = new SubtitleForm(store, store.Load());
            form.Show(); Application.DoEvents();
            var overlay = Field<LyricOverlay>(form, "_overlay");
            var session = Field<LiveSession>(form, "_session");
            Check(!form.Visible && overlay.Visible && IsWindowVisible(overlay.Handle), "normal startup did not show lyrics with the control panel hidden");
            Check(overlay.FormBorderStyle == FormBorderStyle.None && overlay.TopMost, "default lyrics lost their borderless always-on-top window");
            Check((GetWindowLong(overlay.Handle, GwlExStyle) & WsExLayered) != 0, "visible lyrics do not use a native layered window");
            AssertIdle(session);
            Check(!Directory.Exists(directory), "opening default lyrics wrote settings, credentials or history");
            using (var initial = overlay.CaptureFrame()) AssertAlpha(initial, "default lyrics");

            var menu = Field<ContextMenuStrip>(form, "_menu");
            menu.Items.Find("controlPanel", false).Single().PerformClick(); Application.DoEvents();
            Check(form.Visible && !overlay.Visible, "control panel menu did not switch away from lyrics");
            foreach (var name in new[] { "_start", "_stop", "_configure", "_historyButton", "_clearButton" })
                Check(Field<Button>(form, name).Visible, "old control is unavailable: " + name);
            Check(Field<Button>(form, "_start").Enabled && !Field<Button>(form, "_stop").Enabled && Field<Button>(form, "_configure").Enabled,
                "idle start/stop/settings controls changed state during the switch");
            Check(Descendants(form).OfType<RichTextBox>().Count() == 3 &&
                Descendants(form).OfType<ComboBox>().Any(control => control.Name == "interfaceLanguage" && control.Visible),
                "control panel lost transcript, translation, live text or language selection");
            AssertIdle(session);
            if (destination is not null)
            {
                Directory.CreateDirectory(destination);
                using var panel = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(panel, new Rectangle(Point.Empty, form.Size));
                panel.Save(Path.Combine(destination, "lyrics-control-panel.png"), ImageFormat.Png);
            }

            Descendants(form).OfType<Button>().Single(control => control.Name == "showLyrics").PerformClick(); Application.DoEvents();
            Check(!form.Visible && overlay.Visible, "control panel button did not return to lyrics");
            menu.Items.Find("controlPanel", false).Single().PerformClick(); Application.DoEvents();
            menu.Items.Find("showLyrics", false).Single().PerformClick(); Application.DoEvents();
            Check(!form.Visible && overlay.Visible, "tray menu did not restore lyrics");
            AssertIdle(session);
            Check(!Directory.Exists(directory), "switching views wrote persistent state");

            form.Preview(
                new("demo-1", 1, 1, DateTimeOffset.UtcNow, "You do not have to understand every word to follow the story.", true,
                    "你不必听懂每一个词，也能跟上故事的内容。", "completed"),
                new("demo-2", 1, 2, DateTimeOffset.UtcNow, "Try listening for the main idea first.", false));
            Application.DoEvents();
            using var frame = overlay.CaptureFrame();
            AssertAlpha(frame, "bilingual live lyrics");
            AssertToolbarArea(frame, Field<LyricRenderer.Frame>(overlay, "_layout").Toolbar, Field<bool>(overlay, "_toolbarVisible"));
            Check(frame.Size == overlay.ClientSize, "preview bitmap does not match the presented layered surface");
            AssertCompositing(frame, destination);
            // Keep the constrained-height regression independent of the test monitor.
            var compact = LyricRenderer.Render(new Size(1056, 170), 1,
                "You do not have to understand every word to follow the story.", "你不必听懂每一个词，也能跟上故事的内容。",
                "Try listening for the main idea first.", "Synthetic subtitle preview", false, false, 20);
            using (compact.Image)
            {
                AssertAlpha(compact.Image, "compact lyrics");
                AssertToolbarArea(compact.Image, compact.Toolbar, false);
            }
            overlay.SetContent("Listen for the rhythm.", "听听这段旋律。", "", "", true, false, 20);
            Application.DoEvents();
            using (var watching = overlay.CaptureFrame())
            {
                AssertAlpha(watching, "active bilingual lyrics");
                AssertCompositing(watching, destination, "lyrics-watching");
            }
            AssertIdle(session);
            form.Close(); Pump(() => form.IsDisposed);
            Check(overlay.IsDisposed, "closing the application left the lyric window alive");
        }
        finally
        {
            AppText.Language = previousLanguage;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AssertIdle(LiveSession session)
    {
        Check(!session.Active && Field<object?>(session, "_current") is null && Field<long>(session, "_generation") == 0,
            "startup, preview or view switching attempted an audio/API session");
    }

    internal static void AssertToolbarArea(Bitmap frame, Rectangle toolbar, bool visible)
    {
        var controls = Rectangle.Inflate(toolbar, 3, 3);
        for (int y = Math.Max(0, toolbar.Top); y < Math.Min(frame.Height, toolbar.Bottom); y++)
            for (int x = 0; x < frame.Width; x++)
                if (!visible || x < controls.Left || x >= controls.Right)
                    Check(frame.GetPixel(x, y).A <= 8, "hidden top toolbar painted pixels, or subtitles overlap its reserved row");
    }

    internal static void AssertAlpha(Bitmap frame, string scenario)
    {
        Check(Image.IsAlphaPixelFormat(frame.PixelFormat), scenario + " returned an opaque bitmap format");
        int transparent = 0, opaque = 0, partial = 0, bright = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                var pixel = frame.GetPixel(x, y);
                if (pixel.A == 0) transparent++;
                else
                {
                    if (pixel.A == 255) opaque++; else partial++;
                    if (pixel.A > 160 && Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) > 180) bright++;
                }
            }
        Check(transparent > frame.Width * frame.Height * 0.65, scenario + " painted an opaque panel behind the lyrics");
        Check(opaque > 50 && partial > 50 && bright > 50, scenario + " has missing/invisible glyphs or broken alpha antialiasing");
        foreach (var point in new[] { Point.Empty, new Point(frame.Width - 1, 0), new Point(0, frame.Height - 1), new Point(frame.Width - 1, frame.Height - 1) })
            Check(frame.GetPixel(point.X, point.Y).A == 0, scenario + " has an opaque window corner");
        // UpdateLayeredWindow consumes premultiplied BGRA. A raw alpha bitmap with
        // unscaled RGB can look correct in a PNG while rendering bright fringes.
        if (frame.PixelFormat == PixelFormat.Format32bppPArgb)
        {
            var data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, frame.PixelFormat);
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int x = 0; x < frame.Width; x++)
                    {
                        var offset = x * 4;
                        Check(row[offset] <= row[offset + 3] && row[offset + 1] <= row[offset + 3] && row[offset + 2] <= row[offset + 3],
                            scenario + " contains non-premultiplied pixels in the native alpha surface");
                    }
                }
            }
            finally { frame.UnlockBits(data); }
        }
    }

    internal static void AssertCompositing(Bitmap frame, string? destination, string prefix = "lyrics")
    {
        using var lightBackground = Background(frame.Size, true);
        using var darkBackground = Background(frame.Size, false);
        using var light = Composite(lightBackground, frame);
        using var dark = Composite(darkBackground, frame);
        int textPixels = 0, unchanged = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                var source = frame.GetPixel(x, y);
                if (source.A == 0)
                {
                    Check(light.GetPixel(x, y) == lightBackground.GetPixel(x, y) && dark.GetPixel(x, y) == darkBackground.GetPixel(x, y),
                        "fully transparent lyrics changed the synthetic video background");
                    unchanged++;
                }
                else if (source.A == 255 && Math.Max(source.R, Math.Max(source.G, source.B)) > 180)
                {
                    Check(light.GetPixel(x, y) == dark.GetPixel(x, y), "opaque glyph colour depends on the video background");
                    textPixels++;
                }
            }
        Check(unchanged > frame.Width * frame.Height * 0.65 && textPixels > 50, "bright/dark compositing lost transparency or readable text");
        if (destination is null) return;
        Directory.CreateDirectory(destination);
        frame.Save(Path.Combine(destination, prefix + "-alpha.png"), ImageFormat.Png);
        light.Save(Path.Combine(destination, prefix + "-on-bright-video.png"), ImageFormat.Png);
        dark.Save(Path.Combine(destination, prefix + "-on-dark-video.png"), ImageFormat.Png);
    }

    internal static Bitmap Background(Size size, bool light)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var brush = new LinearGradientBrush(new Rectangle(Point.Empty, size),
            light ? Color.FromArgb(239, 237, 218) : Color.FromArgb(12, 23, 40),
            light ? Color.FromArgb(174, 203, 213) : Color.FromArgb(43, 58, 77), 25);
        graphics.FillRectangle(brush, new Rectangle(Point.Empty, size));
        using var shape = new SolidBrush(light ? Color.FromArgb(195, 205, 180) : Color.FromArgb(26, 44, 59));
        graphics.FillEllipse(shape, size.Width / 3, -size.Height / 2, size.Width / 2, size.Height * 2);
        return bitmap;
    }

    internal static Bitmap Composite(Bitmap background, Bitmap frame)
    {
        var result = (Bitmap)background.Clone();
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.DrawImageUnscaled(frame, Point.Empty);
        return result;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(control => new[] { control }.Concat(Descendants(control)));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(completed(), "lyric application close timed out");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
