using System.Reflection;
using System.Runtime.InteropServices;
using ListeningSubtitles.Core;
using ListeningSubtitles.UI;

internal static class LyricToolbarTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int WsExLayered = 0x00080000, WsExTransparent = 0x20;

    public static void Ui(string? destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Listening-toolbar-test-" + Guid.NewGuid().ToString("N"));
        var previousLanguage = AppText.Language;
        var store = new ListeningSettingsStore(directory);
        try
        {
            using (var form = new SubtitleForm(store, store.Load()))
            {
                form.Show(); Application.DoEvents();
                var overlay = Field<LyricOverlay>(form, "_overlay");
                var menu = Field<ContextMenuStrip>(form, "_menu");
                var toggles = 0; overlay.ToggleRequested += () => toggles++;
                overlay.SetContent("The chorus is about to begin.", "副歌即将开始。", "", "", false, false, 20, "上句译文 · 跟上这段旋律。");
                HideHover(overlay);
                var hiddenLayout = Layout(overlay);
                using var hidden = overlay.CaptureFrame();
                Check(!Field<bool>(overlay, "_toolbarVisible"), "toolbar is permanently visible before hovering");
                Hover(overlay, Center(hiddenLayout.HoverArea));
                var shownLayout = Layout(overlay);
                using var shown = overlay.CaptureFrame();
                Check(Field<bool>(overlay, "_toolbarVisible"), "hovering over the lyrics did not reveal the top toolbar");
                AssertStableCaptions(hidden, shown, shownLayout.Toolbar);
                LyricOverlayTests.AssertToolbarArea(hidden, hiddenLayout.Toolbar, false);
                LyricOverlayTests.AssertToolbarArea(shown, shownLayout.Toolbar, true);
                foreach (var action in new[] { LyricAction.Toggle, LyricAction.Smaller, LyricAction.Larger, LyricAction.Lock, LyricAction.Menu })
                {
                    var bounds = shownLayout.BoundsFor(action);
                    Check(!bounds.IsEmpty && shownLayout.Toolbar.Contains(bounds) && shownLayout.HitTest(Center(bounds)) == action,
                        "shown toolbar has an absent or incorrectly bound action: " + action);
                    Check(hiddenLayout.HitTest(Center(bounds)) == LyricAction.None, "hidden toolbar still accepts an action");
                }
                LyricOverlayTests.AssertCompositing(hidden, destination, "lyrics-toolbar-hidden");
                LyricOverlayTests.AssertCompositing(shown, destination, "lyrics-toolbar-visible");
                var steadyFrame = Field<Bitmap>(overlay, "_frame");
                Hover(overlay, Center(shownLayout.HoverArea));
                Check(ReferenceEquals(steadyFrame, Field<Bitmap>(overlay, "_frame")), "stationary hover continuously rebuilt the alpha surface");
                HideHover(overlay);
                Check(!Field<bool>(overlay, "_toolbarVisible"), "toolbar did not hide when the pointer left the lyric area");
                overlay.Hide(); Space(overlay);
                Check(toggles == 0 && !Field<bool>(overlay, "_toolbarVisible"), "hidden lyrics retained a toolbar or accepted a start action");
                overlay.Show(); HideHover(overlay);
                using (var reshown = overlay.CaptureFrame()) LyricOverlayTests.AssertToolbarArea(reshown, Layout(overlay).Toolbar, false);
                overlay.WindowState = FormWindowState.Minimized; Space(overlay);
                Check(toggles == 0, "minimized lyrics accepted the keyboard start action");
                overlay.WindowState = FormWindowState.Normal;

                var larger = ActionPoint(overlay, LyricAction.Larger);
                Mouse(overlay, "OnMouseDown", larger);
                FontIs(form, store, 20);
                Mouse(overlay, "OnMouseUp", larger); FontIs(form, store, 22);
                var smaller = ActionPoint(overlay, LyricAction.Smaller);
                Mouse(overlay, "OnMouseDown", smaller);
                Mouse(overlay, "OnMouseUp", Center(Layout(overlay).BoundsFor(LyricAction.Larger))); FontIs(form, store, 22);
                Mouse(overlay, "OnMouseUp", Center(Layout(overlay).BoundsFor(LyricAction.Larger))); FontIs(form, store, 22);
                smaller = ActionPoint(overlay, LyricAction.Smaller);
                Mouse(overlay, "OnMouseDown", smaller);
                Mouse(overlay, "OnMouseUp", new Point(0, overlay.Height - 1)); FontIs(form, store, 22);
                smaller = ActionPoint(overlay, LyricAction.Smaller);
                Mouse(overlay, "OnMouseDown", smaller);
                var beforeResize = overlay.Size;
                overlay.Width--;
                Mouse(overlay, "OnMouseUp", smaller); FontIs(form, store, 22);
                overlay.Size = beforeResize;
                Click(overlay, LyricAction.Smaller); FontIs(form, store, 20);
                for (int i = 0; i < 10; i++) Click(overlay, LyricAction.Larger);
                FontIs(form, store, 36);
                for (int i = 0; i < 14; i++) Click(overlay, LyricAction.Smaller);
                FontIs(form, store, 12);
                for (int i = 0; i < 4; i++) Click(overlay, LyricAction.Larger);
                FontIs(form, store, 20);

                var lockedCenter = new Point(overlay.Left + overlay.Width / 2, overlay.Top + overlay.Height / 2);
                var lockPoint = ActionPoint(overlay, LyricAction.Lock);
                Mouse(overlay, "OnMouseDown", lockPoint);
                Check(!overlay.LyricsLocked, "lock was activated on mouse-down rather than release");
                Mouse(overlay, "OnMouseUp", lockPoint);
                AssertLocked(overlay, true);
                Check(store.Load().LyricsLocked && store.Load().LockedLyricCenter == lockedCenter,
                    "lock or lyric center did not round-trip through saved preferences");
                Hover(overlay, Center(Layout(overlay).HoverArea));
                Check(!Field<bool>(overlay, "_toolbarVisible"), "locked lyrics revealed a mouse toolbar");
                Mouse(overlay, "OnMouseDown", Center(Layout(overlay).BoundsFor(LyricAction.Larger)));
                Mouse(overlay, "OnMouseUp", Center(Layout(overlay).BoundsFor(LyricAction.Larger)));
                FontIs(form, store, 20);
                Space(overlay);
                Check(toggles == 0, "locked lyrics accepted the keyboard start/stop action");

                menu.Items["lockLyrics"]!.PerformClick();
                AssertLocked(overlay, false); Check(!store.Load().LyricsLocked, "tray unlock was not saved");
                Hover(overlay, Center(Layout(overlay).HoverArea));
                Check(Field<bool>(overlay, "_toolbarVisible"), "tray unlock did not restore hover controls");
                Click(overlay, LyricAction.Lock); AssertLocked(overlay, true);
                AssertIdle(form);
                form.Close(); Pump(() => form.IsDisposed);
            }

            var restoredSettings = store.Load();
            Check(restoredSettings.LyricsLocked && restoredSettings.LockedLyricCenter is not null, "saved lock was lost before restart");
            using (var restarted = new SubtitleForm(store, restoredSettings))
            {
                restarted.Show(); Application.DoEvents();
                var overlay = Field<LyricOverlay>(restarted, "_overlay");
                AssertLocked(overlay, true);
                var center = restoredSettings.LockedLyricCenter!.Value;
                var area = Screen.FromPoint(center).WorkingArea;
                var expected = new Point(Math.Clamp(center.X - overlay.Width / 2, area.Left, Math.Max(area.Left, area.Right - overlay.Width)),
                    Math.Clamp(center.Y - overlay.Height / 2, area.Top, Math.Max(area.Top, area.Bottom - overlay.Height)));
                Check(overlay.Location == expected, "restarted locked lyrics did not restore their saved center within the working area");
                Check(!Field<bool>(overlay, "_toolbarVisible"), "restarted locked lyrics displayed an active toolbar");
                Field<ContextMenuStrip>(restarted, "_menu").Items["lockLyrics"]!.PerformClick();
                AssertLocked(overlay, false); Check(!store.Load().LyricsLocked, "restarted lyrics could not be unlocked from the tray");
                AssertIdle(restarted);
                restarted.Close(); Pump(() => restarted.IsDisposed);
            }
        }
        finally
        {
            AppText.Language = previousLanguage;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AssertStableCaptions(Bitmap hidden, Bitmap shown, Rectangle toolbar)
    {
        Check(hidden.Size == shown.Size, "showing the toolbar changed the lyric window size");
        var toolbarPixels = Rectangle.Inflate(toolbar, 3, 3);
        int changed = 0;
        for (int y = 0; y < hidden.Height; y++)
            for (int x = 0; x < hidden.Width; x++)
                if (hidden.GetPixel(x, y) != shown.GetPixel(x, y))
                {
                    Check(toolbarPixels.Contains(x, y), "hovering shifted or redrew the subtitle content outside the toolbar");
                    changed++;
                }
        Check(changed > 100, "visible toolbar was not rendered into the native alpha frame");
    }

    private static void FontIs(SubtitleForm form, ListeningSettingsStore store, int expected)
    {
        Check(Field<ListeningSettings>(form, "_settings").FontSize == expected && store.Load().FontSize == expected &&
            Field<int>(Field<LyricOverlay>(form, "_overlay"), "_fontSize") == expected,
            "font release gesture, limit or saved preference is incorrect");
    }
    private static void AssertLocked(LyricOverlay overlay, bool expected)
    {
        var style = GetWindowLong(overlay.Handle, -20);
        Check(overlay.LyricsLocked == expected && ((style & WsExTransparent) != 0) == expected && (style & WsExLayered) != 0,
            "lyric lock state and native layered/click-through styles disagree");
    }
    private static void AssertIdle(SubtitleForm form)
    {
        var session = Field<LiveSession>(form, "_session");
        Check(!session.Active && Field<long>(session, "_generation") == 0, "synthetic toolbar checks started an audio/API session");
    }
    private static LyricRenderer.Frame Layout(LyricOverlay overlay) => Field<LyricRenderer.Frame>(overlay, "_layout");
    private static Point ActionPoint(LyricOverlay overlay, LyricAction action)
    {
        Hover(overlay, Center(Layout(overlay).HoverArea));
        return Center(Layout(overlay).BoundsFor(action));
    }
    private static void Click(LyricOverlay overlay, LyricAction action)
    {
        var point = ActionPoint(overlay, action);
        Mouse(overlay, "OnMouseDown", point); Mouse(overlay, "OnMouseUp", point);
    }
    private static void Mouse(LyricOverlay overlay, string method, Point point) => typeof(LyricOverlay).GetMethod(method, Private)!
        .Invoke(overlay, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
    private static void Space(LyricOverlay overlay) => typeof(LyricOverlay).GetMethod("ProcessCmdKey", Private)!.Invoke(overlay,
        new object[] { Message.Create(overlay.Handle, 0x100, new IntPtr((int)Keys.Space), IntPtr.Zero), Keys.Space });
    private static void Hover(LyricOverlay overlay, Point point) => typeof(LyricOverlay).GetMethod("UpdateHover", Private)!.Invoke(overlay, new object[] { point });
    private static void HideHover(LyricOverlay overlay)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        do
        {
            Hover(overlay, new Point(-100, -100));
            if (!Field<bool>(overlay, "_toolbarVisible")) return;
            Thread.Sleep(10);
        } while (DateTime.UtcNow < deadline);
        Check(false, "toolbar did not hide after its pointer-leave grace period");
    }
    private static Point Center(Rectangle rectangle) => new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(completed(), "toolbar application close timed out");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
}
