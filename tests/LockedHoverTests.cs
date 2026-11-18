using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ListeningSubtitles.Core;
using ListeningSubtitles.UI;

internal static class LockedHoverTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int WsExLayered = 0x80000, WsExTransparent = 0x20, WsExNoActivate = 0x08000000;

    public static void Ui(string? destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Listening-unlock-test-" + Guid.NewGuid().ToString("N"));
        var previousLanguage = AppText.Language;
        using var backdrop = new Form
        {
            AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen!.WorkingArea,
            ShowInTaskbar = false, TopMost = true, BackColor = Color.FromArgb(12, 23, 40)
        };
        try
        {
            backdrop.Show(); Application.DoEvents();
            var store = new ListeningSettingsStore(directory);
            store.Save(new ListeningSettings { LyricsLocked = true });
            Form? previousHelper = null;
            using (var form = new SubtitleForm(store, store.Load()))
            {
                form.Show(); Application.DoEvents();
                var overlay = Field<LyricOverlay>(form, "_overlay");
                var menu = Field<ContextMenuStrip>(form, "_menu");
                var toggles = 0; overlay.ToggleRequested += () => toggles++;
                overlay.SetContent("The chorus is about to begin.", "副歌即将开始。", "", "", false, false, 20, "上句译文 · 跟上这段旋律。");
                overlay.BringToFront();
                var mainSurface = Field<Bitmap>(overlay, "_frame");
                var helper = Reveal(overlay);
                previousHelper = helper;
                AssertLocked(overlay);
                Check(ReferenceEquals(mainSurface, Field<Bitmap>(overlay, "_frame")) && !Field<bool>(overlay, "_toolbarVisible"),
                    "locked hover repainted the main lyrics or revealed the full toolbar");
                Check(!helper.ShowInTaskbar && (GetWindowLong(helper.Handle, -20) & WsExNoActivate) != 0 && GetWindow(helper.Handle, 4) == overlay.Handle,
                    "unlock control is not a separate non-activating owned utility window");
                Check(helper.Width < overlay.Width / 2 && helper.Height < overlay.Height / 3 && overlay.OwnedForms.Count(child => child.Visible) == 1,
                    "locked hover opened more than the small unlock surface");
                using (var frame = overlay.CaptureFrame())
                {
                    var glyph = GlyphPoint(frame, overlay, helper);
                    var probes = new[]
                    {
                        ("glyph", glyph), ("whitespace", overlay.PointToScreen(new Point(overlay.Width - 2, overlay.Height / 2))),
                        ("main corner", overlay.PointToScreen(new Point(1, 1))),
                        ("footer", overlay.PointToScreen(new Point(overlay.Width / 2, overlay.Height - 2))),
                        ("rounded button corner", helper.PointToScreen(OutsideCorner(helper)))
                    };
                    foreach (var (name, point) in probes)
                    {
                        AssertLocked(overlay);
                        Check(WindowFromPoint(new NativePoint(point.X, point.Y)) == backdrop.Handle,
                            name + " did not pass through the locked lyric surfaces to the independent test window");
                    }
                }
                var helperHit = WindowFromPoint(new NativePoint(helper.Left + helper.Width / 2, helper.Top + helper.Height / 2));
                Check(helperHit == helper.Handle || IsChild(helper.Handle, helperHit), "the unlock button itself is not independently clickable");
                Export(overlay, helper, destination);

                overlay.Hide(); Check(!helper.Visible, "unlock helper remained visible after hiding lyrics");
                overlay.Show(); helper = Reveal(overlay);
                overlay.WindowState = FormWindowState.Minimized;
                Hover(overlay); Check(!helper.Visible, "unlock helper remained visible for minimized lyrics");
                overlay.WindowState = FormWindowState.Normal; helper = Reveal(overlay);
                overlay.Enabled = false; Hover(overlay); Check(!helper.Visible, "disabled lyrics retained an actionable unlock helper");
                overlay.Enabled = true; helper = Reveal(overlay);
                using (var modal = new Form { ShowInTaskbar = false, Size = new(260, 140), StartPosition = FormStartPosition.CenterParent })
                {
                    modal.Shown += (_, _) =>
                    {
                        Hover(overlay);
                        Check(!helper.Visible, "a modal dialog retained the unlock helper");
                        modal.BeginInvoke((Action)modal.Close);
                    };
                    modal.ShowDialog(overlay);
                }
                helper = Reveal(overlay);
                menu.Items["controlPanel"]!.PerformClick();
                Check(form.Visible && !helper.Visible, "opening the control panel left the unlock helper visible");
                menu.Items["showLyrics"]!.PerformClick(); helper = Reveal(overlay);
                Field<ComboBox>(form, "_interfaceLanguage").SelectedIndex = 1;
                helper = Reveal(overlay);
                Check((helper.AccessibleName + " " + helper.Text).Contains("Unlock", StringComparison.OrdinalIgnoreCase),
                    "unlock helper did not follow the English interface language");

                var center = new Point(helper.ClientSize.Width / 2, helper.ClientSize.Height / 2);
                Mouse(helper, "OnMouseUp", center); AssertLocked(overlay);
                Mouse(helper, "OnMouseDown", center);
                Mouse(helper, "OnMouseUp", new Point(-10, -10)); AssertLocked(overlay);
                Mouse(helper, "OnMouseDown", center, MouseButtons.Right);
                Mouse(helper, "OnMouseUp", center, MouseButtons.Right); AssertLocked(overlay);
                Mouse(helper, "OnMouseDown", center); AssertLocked(overlay);
                Mouse(helper, "OnMouseUp", center);
                Check(!overlay.LyricsLocked && !helper.Visible && !store.Load().LyricsLocked && toggles == 0,
                    "matched helper release did not unlock through the saved lock handler, or started capture");
                Hover(overlay);
                Check(Field<bool>(overlay, "_toolbarVisible"), "unlocking did not restore the existing full hover toolbar");
                menu.Items["lockLyrics"]!.PerformClick(); helper = Reveal(overlay); AssertLocked(overlay);
                Check(store.Load().LockedLyricCenter == new Point(overlay.Left + overlay.Width / 2, overlay.Top + overlay.Height / 2),
                    "helper unlock/relock lost the saved lyric center");
                AssertIdle(form);
                form.Close(); Pump(() => form.IsDisposed);
                Check(helper.IsDisposed, "disposing the main application left an unlock window alive");
            }
            Check(previousHelper!.IsDisposed, "the first unlock window leaked across restart");
            using (var restarted = new SubtitleForm(store, store.Load()))
            {
                restarted.Show(); Application.DoEvents();
                var overlay = Field<LyricOverlay>(restarted, "_overlay");
                var helper = Reveal(overlay);
                AssertLocked(overlay);
                Check(store.Load().InterfaceLanguage == "en" && (helper.AccessibleName + " " + helper.Text).Contains("Unlock", StringComparison.OrdinalIgnoreCase),
                    "restarted locked helper lost the saved language or lock state");
                AssertIdle(restarted);
                restarted.Close(); Pump(() => restarted.IsDisposed);
                Check(helper.IsDisposed, "restarted application did not dispose its unlock helper");
            }
        }
        finally
        {
            AppText.Language = previousLanguage;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Form Reveal(LyricOverlay overlay)
    {
        Hover(overlay);
        var helper = Field<Form?>(overlay, "_unlockButton");
        Check(helper is { Visible: true }, "locked lyric hover did not show its separate unlock button");
        return helper!;
    }
    private static void Hover(LyricOverlay overlay)
    {
        var area = Field<LyricRenderer.Frame>(overlay, "_layout").HoverArea;
        typeof(LyricOverlay).GetMethod("UpdateHover", Private)!.Invoke(overlay,
            new object[] { new Point(area.Left + area.Width / 2, area.Top + area.Height / 2) });
    }
    private static void Export(LyricOverlay overlay, Form helper, string? destination)
    {
        if (destination is null) return;
        Check(overlay.Bounds.Contains(helper.Bounds), "unlock helper lies outside the illustrative composition bounds");
        using var main = overlay.CaptureFrame();
        using var button = (Bitmap)helper.GetType().GetMethod("CaptureFrame", Private | BindingFlags.Public)!.Invoke(helper, null)!;
        using var background = LyricOverlayTests.Background(main.Size, false);
        using var result = LyricOverlayTests.Composite(background, main);
        using (var graphics = Graphics.FromImage(result)) graphics.DrawImageUnscaled(button, helper.Left - overlay.Left, helper.Top - overlay.Top);
        Directory.CreateDirectory(destination);
        result.Save(Path.Combine(destination, "lyrics-locked-hover-on-dark-video.png"), ImageFormat.Png);
    }
    private static Point GlyphPoint(Bitmap image, LyricOverlay overlay, Form helper)
    {
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).A > 180)
                {
                    var point = overlay.PointToScreen(new Point(x, y));
                    if (!helper.Bounds.Contains(point)) return point;
                }
        throw new Exception("no visible lyric glyph was available for the native pass-through probe");
    }
    private static Point OutsideCorner(Form helper) => new[] { Point.Empty, new Point(helper.Width - 1, 0), new Point(0, helper.Height - 1), new Point(helper.Width - 1, helper.Height - 1) }
        .First(point => !helper.Region!.IsVisible(point));
    private static void AssertLocked(LyricOverlay overlay) => Check(overlay.LyricsLocked &&
        (GetWindowLong(overlay.Handle, -20) & (WsExLayered | WsExTransparent)) == (WsExLayered | WsExTransparent),
        "locked-hover processing cleared the native main-window pass-through style");
    private static void AssertIdle(SubtitleForm form)
    {
        var session = Field<LiveSession>(form, "_session");
        Check(!session.Active && Field<long>(session, "_generation") == 0, "unlock checks attempted an audio/API session");
    }
    private static void Mouse(Form helper, string method, Point point, MouseButtons button = MouseButtons.Left) => helper.GetType().GetMethod(method, Private)!
        .Invoke(helper, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(completed(), "unlock application close timed out");
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePoint(int x, int y) { public readonly int X = x, Y = y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr child);
}
