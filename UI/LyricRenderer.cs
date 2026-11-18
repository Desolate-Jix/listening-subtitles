using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

internal enum LyricAction { None, Toggle, Smaller, Larger, Lock, Menu }

/// <summary>Produces the premultiplied-alpha surface used both on screen and by the offline preview.</summary>
internal static class LyricRenderer
{
    internal sealed class Frame(Bitmap image, Rectangle toolbar, Rectangle hoverArea, Rectangle[] actions, bool toolbarVisible)
    {
        internal Bitmap Image { get; } = image;
        internal Rectangle Toolbar { get; } = toolbar;
        internal Rectangle HoverArea { get; } = hoverArea;
        internal Rectangle BoundsFor(LyricAction action) => (int)action > 0 && (int)action < actions.Length ? actions[(int)action] : Rectangle.Empty;
        internal LyricAction HitTest(Point point)
        {
            if (toolbarVisible)
                for (var index = 1; index < actions.Length; index++)
                    if (actions[index].Contains(point)) return (LyricAction)index;
            return LyricAction.None;
        }
    }

    internal static Frame Render(Size size, float scale, string original, string translation, string live,
        string status, bool active, bool busy, int fontSize, string previous = "", bool toolbarVisible = false, LyricAction hot = LyricAction.None)
    {
        previous = (previous ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var margin = 24 * scale;
        var contentWidth = Math.Max(40, size.Width - margin * 2);
        var originalPixels = Math.Clamp(fontSize * 1.55f, 20, 56) * scale;
        var secondaryPixels = originalPixels * .83f;
        using var chromeFont = FontAt(13 * scale, FontStyle.Regular);
        var toggleLabel = busy ? AppText.T("请稍候", "Please wait") : active ? AppText.T("停止", "Stop") : AppText.T("开始", "Start");
        var buttonSide = Math.Max(1, (int)Math.Ceiling(34 * scale));
        var toggleWidth = (int)Math.Ceiling(graphics.MeasureString(toggleLabel, chromeFont).Width + 38 * scale);
        var maxToolbarWidth = Math.Max(1, size.Width - (int)Math.Ceiling(16 * scale));
        var showToggleLabel = toggleWidth + buttonSide * 4 <= maxToolbarWidth;
        var cellWidth = Math.Min(buttonSide, Math.Max(1, maxToolbarWidth / 5));
        var toolbarWidth = showToggleLabel ? toggleWidth + buttonSide * 4 : cellWidth * 5;
        var toolbarHeight = buttonSide;
        var hasText = !string.IsNullOrWhiteSpace(original) || !string.IsNullOrWhiteSpace(translation);
        var main = string.IsNullOrWhiteSpace(original)
            ? (hasText || !string.IsNullOrWhiteSpace(live) ? "" : AppText.T("播放音频后，点击开始", "Play audio, then select Start"))
            : original;
        using var liveFont = FontAt(Math.Clamp(originalPixels * .48f, 13 * scale, 19 * scale), FontStyle.Regular);
        using var statusFont = FontAt(12 * scale, FontStyle.Regular);
        var gap = 6 * scale;
        // Always reserve the same toolbar budget, including when it is completely hidden.
        // The final toolbar is placed directly above the words, not at the window's top.
        var toolbarGap = 7 * scale;
        var textTop = 6 * scale + toolbarHeight + toolbarGap;
        var textBottom = Math.Max(textTop, size.Height - 9 * scale);
        var availableHeight = textBottom - textTop;
        var hasPrevious = !string.IsNullOrWhiteSpace(previous);
        var reserveTranslation = hasPrevious && !string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(status);
        var statusHeight = TextHeight(graphics, status, statusFont, contentWidth, 1);
        var lines = 2;
        var factor = 1f;
        // A small working area must never push words into the controls. Prefer a single
        // elided line for each language, then fit the three intentional lyric rows.
        // A transient/error status joins this budget so a compact layout can retain it.
        // A live preview only uses the space left after these intentional rows.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var probeMain = FitFont(graphics, main, originalPixels * factor, contentWidth, lines, FontStyle.Bold);
            using var probeSecondary = FitFont(graphics, translation, secondaryPixels * factor, contentWidth, lines, FontStyle.Regular);
            using var probePrevious = FontAt(Math.Clamp(originalPixels * factor * .66f, 14 * scale, 22 * scale), FontStyle.Regular);
            var heights = new[]
            {
                hasPrevious ? TextHeight(graphics, previous, probePrevious, contentWidth, 1) : 0,
                TextHeight(graphics, main, probeMain, contentWidth, lines),
                TranslationHeight(graphics, translation, probeSecondary, contentWidth, lines, reserveTranslation),
                statusHeight
            };
            var count = heights.Count(height => height > 0);
            if (heights.Sum() + Math.Max(0, count - 1) * gap <= availableHeight) break;
            if (lines == 2) { lines = 1; continue; }
            factor *= .9f;
        }
        using var mainFont = FitFont(graphics, main, originalPixels * factor, contentWidth, lines, FontStyle.Bold);
        using var secondaryFont = FitFont(graphics, translation, secondaryPixels * factor, contentWidth, lines, FontStyle.Regular);
        using var previousFont = FontAt(Math.Clamp(originalPixels * factor * .66f, 14 * scale, 22 * scale), FontStyle.Regular);
        var mainHeight = TextHeight(graphics, main, mainFont, contentWidth, lines);
        var secondaryHeight = TranslationHeight(graphics, translation, secondaryFont, contentWidth, lines, reserveTranslation);
        // A partial is a quiet preview; a completed sentence remains the primary lyric.
        var preview = !string.IsNullOrWhiteSpace(live) && !string.Equals(live.Trim(), original.Trim(), StringComparison.Ordinal) ? live : "";
        var previewHeight = TextHeight(graphics, preview, liveFont, contentWidth, 1);
        var rows = new List<(string Text, Font Font, int Height, Color Color, int Lines, bool Soft)>();
        if (hasPrevious) rows.Add((previous, previousFont, TextHeight(graphics, previous, previousFont, contentWidth, 1), Color.FromArgb(192, 207, 229), 1, true));
        if (mainHeight > 0) rows.Add((main, mainFont, mainHeight, hasText ? Color.White : Color.FromArgb(223, 230, 237), lines, false));
        if (secondaryHeight > 0) rows.Add((translation, secondaryFont, secondaryHeight, Color.FromArgb(185, 229, 229), lines, false));
        var required = rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * gap;
        if (statusHeight > 0 && required + statusHeight + (rows.Count > 0 ? gap : 0) <= availableHeight)
            required += statusHeight + (rows.Count > 0 ? gap : 0);
        else statusHeight = 0;
        if (previewHeight > 0 && required + previewHeight + (rows.Count > 0 || statusHeight > 0 ? gap : 0) <= availableHeight)
            rows.Add((preview, liveFont, previewHeight, Color.FromArgb(197, 207, 220), 1, true));
        if (statusHeight > 0) rows.Add((status, statusFont, statusHeight, Color.FromArgb(255, 209, 153), 1, true));
        var total = rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * gap;
        var firstRowY = Math.Max(textTop, textBottom - total);
        var y = firstRowY;
        var textState = graphics.Save();
        graphics.SetClip(new RectangleF(0, Math.Max(0, textTop - 2 * scale), size.Width, Math.Max(0, size.Height - textTop + 2 * scale)));
        foreach (var row in rows)
        {
            DrawOutlinedText(graphics, row.Text, row.Font, new RectangleF(margin, y, contentWidth, row.Height), row.Color, scale, row.Lines, true, row.Soft);
            y += row.Height + gap;
        }
        graphics.Restore(textState);
        var toolbar = new Rectangle((size.Width - toolbarWidth) / 2,
            Math.Max(0, (int)Math.Floor(firstRowY - toolbarGap) - toolbarHeight), toolbarWidth, toolbarHeight);
        var actions = new Rectangle[(int)LyricAction.Menu + 1];
        for (var index = 0; index < 5; index++)
        {
            if (showToggleLabel)
                actions[index + 1] = new Rectangle(index == 0 ? toolbar.Left : toolbar.Left + toggleWidth + (index - 1) * buttonSide,
                    toolbar.Top, index == 0 ? toggleWidth : buttonSide, buttonSide);
            else
                actions[index + 1] = new Rectangle(toolbar.Left + index * cellWidth, toolbar.Top, cellWidth, buttonSide);
        }
        var textWidth = rows.Where(row => !string.IsNullOrWhiteSpace(row.Text))
            .Select(row => MeasuredTextWidth(graphics, row.Text, row.Font, contentWidth))
            .DefaultIfEmpty(0).Max();
        var hoverWidth = Math.Min(size.Width, (int)Math.Ceiling(Math.Max(toolbar.Width, textWidth) + 24 * scale));
        var hoverArea = Rectangle.Intersect(new Rectangle((size.Width - hoverWidth) / 2, Math.Max(0, toolbar.Top - (int)(4 * scale)),
            hoverWidth, Math.Max(1, (int)Math.Ceiling(y + 5 * scale) - Math.Max(0, toolbar.Top - (int)(4 * scale)))), new Rectangle(Point.Empty, size));
        if (toolbarVisible) DrawToolbar(graphics, toolbar, actions, chromeFont, toggleLabel, showToggleLabel, active, busy, hot, scale);
        return new(bitmap, toolbar, hoverArea, actions, toolbarVisible);
    }

    private static float MeasuredTextWidth(Graphics graphics, string text, Font font, float width)
    {
        using var format = TextFormat();
        return Math.Min(width, graphics.MeasureString(text, font, new SizeF(width, 10000), format).Width);
    }

    private static void DrawToolbar(Graphics graphics, Rectangle toolbar, Rectangle[] actions, Font font,
        string toggleLabel, bool showToggleLabel, bool active, bool busy, LyricAction hot, float scale)
    {
        var iconScale = Math.Min(scale, actions[(int)LyricAction.Smaller].Width / 28f);
        using var compactFont = FontAt(Math.Max(1, 13 * iconScale), FontStyle.Regular);
        using (var path = RoundRect(toolbar, 14 * scale))
        {
            using var fill = new SolidBrush(Color.FromArgb(226, 25, 30, 38));
            using var border = new Pen(Color.FromArgb(112, 158, 174, 190), Math.Max(1, scale));
            graphics.FillPath(fill, path); graphics.DrawPath(border, path);
        }
        if (hot != LyricAction.None && !(hot == LyricAction.Toggle && busy))
        {
            var hover = actions[(int)hot];
            hover.Inflate(-(int)Math.Ceiling(2 * scale), -(int)Math.Ceiling(3 * scale));
            if (hover.Width > 0 && hover.Height > 0)
            {
                using var fill = new SolidBrush(Color.FromArgb(48, 223, 238, 247));
                using var path = RoundRect(hover, 8 * scale);
                graphics.FillPath(fill, path);
            }
        }
        var toggle = actions[(int)LyricAction.Toggle];
        var toggleY = toggle.Top + toggle.Height / 2f;
        var iconX = showToggleLabel ? toggle.Left + 14 * scale : toggle.Left + toggle.Width / 2f;
        using (var icon = new SolidBrush(busy ? Color.FromArgb(165, 183, 198) : active ? Color.FromArgb(247, 180, 170) : Color.FromArgb(174, 239, 216)))
        {
            if (busy)
            {
                using var pen = new Pen(Color.FromArgb(170, 209, 220, 230), 1.8f * scale);
                graphics.DrawArc(pen, iconX - 5 * iconScale, toggleY - 5 * iconScale, 10 * iconScale, 10 * iconScale, -70, 275);
            }
            else if (active) graphics.FillRectangle(icon, iconX - 4 * iconScale, toggleY - 4 * iconScale, 8 * iconScale, 8 * iconScale);
            else graphics.FillPolygon(icon, new PointF[] { new(iconX - 3 * iconScale, toggleY - 5 * iconScale), new(iconX - 3 * iconScale, toggleY + 5 * iconScale), new(iconX + 5 * iconScale, toggleY) });
        }
        if (showToggleLabel)
            DrawOutlinedText(graphics, toggleLabel, font, new RectangleF(toggle.Left + 26 * scale, toggle.Top, toggle.Width - 31 * scale, toggle.Height),
                busy ? Color.FromArgb(182, 193, 207) : Color.FromArgb(241, 245, 250), scale, 1, false);
        foreach (var action in new[] { LyricAction.Smaller, LyricAction.Larger })
        {
            var bounds = actions[(int)action];
            DrawOutlinedText(graphics, action == LyricAction.Smaller ? "A−" : "A+", compactFont, bounds,
                Color.FromArgb(235, 241, 247), iconScale, 1, false);
        }
        var locked = actions[(int)LyricAction.Lock];
        var lockX = locked.Left + locked.Width / 2f;
        var lockY = locked.Top + locked.Height / 2f;
        using (var pen = new Pen(Color.FromArgb(235, 231, 239, 247), Math.Max(.5f, 1.4f * iconScale)))
        {
            graphics.DrawArc(pen, lockX - 4 * iconScale, lockY - 8 * iconScale, 8 * iconScale, 10 * iconScale, 180, 180);
            using var body = RoundRect(new Rectangle((int)Math.Round(lockX - 6 * iconScale), (int)Math.Round(lockY - 2 * iconScale),
                Math.Max(1, (int)Math.Round(12 * iconScale)), Math.Max(1, (int)Math.Round(10 * iconScale))), 2 * iconScale);
            graphics.DrawPath(pen, body);
            graphics.DrawLine(pen, lockX, lockY + 1 * iconScale, lockX, lockY + 4 * iconScale);
        }
        var menu = actions[(int)LyricAction.Menu];
        using var dot = new SolidBrush(Color.FromArgb(235, 238, 244, 250));
        for (var i = -1; i <= 1; i++)
            graphics.FillEllipse(dot, menu.Left + menu.Width / 2f + i * 5 * iconScale - iconScale, menu.Top + menu.Height / 2f - iconScale, 2 * iconScale, 2 * iconScale);
    }

    private static Font FontAt(float pixels, FontStyle style) => new("Microsoft YaHei UI", pixels, style, GraphicsUnit.Pixel);

    private static Font FitFont(Graphics graphics, string text, float pixels, float width, int lines, FontStyle style)
    {
        var font = FontAt(pixels, style);
        if (string.IsNullOrWhiteSpace(text)) return font;
        var minimum = pixels * .74f;
        using var format = TextFormat();
        while (font.Size > minimum && graphics.MeasureString(text, font, new SizeF(width, 10000), format).Height > font.GetHeight(graphics) * lines + 2)
        {
            var nextSize = Math.Max(minimum, font.Size - 1);
            font.Dispose(); font = FontAt(nextSize, style);
        }
        return font;
    }

    private static int TextHeight(Graphics graphics, string text, Font font, float width, int lines)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        using var format = TextFormat();
        var height = graphics.MeasureString(text, font, new SizeF(width, 10000), format).Height;
        return (int)Math.Ceiling(Math.Min(height, font.GetHeight(graphics) * lines) + 2);
    }

    private static int TranslationHeight(Graphics graphics, string text, Font font, float width, int lines, bool reserve)
    {
        var height = TextHeight(graphics, text, font, width, lines);
        // Keep the current sentence in place while its first translated line is pending.
        return reserve ? Math.Max(height, (int)Math.Ceiling(font.GetHeight(graphics)) + 2) : height;
    }

    private static StringFormat TextFormat() => new(StringFormat.GenericDefault)
    {
        Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit
    };

    private static void DrawOutlinedText(Graphics destination, string text, Font font, RectangleF bounds, Color color, float scale, int lines, bool outline = true, bool soft = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // Short centered lines need no full-window mask; retain the full layout width
        // for wrapping/elision so shortening the bitmap cannot change the line breaks.
        if (!text.Contains('\n') && !text.Contains('\r'))
        {
            var naturalWidth = destination.MeasureString(text, font).Width + 4 * scale;
            if (naturalWidth < bounds.Width)
                bounds = new RectangleF(bounds.X + (bounds.Width - naturalWidth) / 2, bounds.Y, naturalWidth, bounds.Height);
        }
        var pad = (int)Math.Ceiling(7 * scale);
        var width = (int)Math.Ceiling(bounds.Width) + pad * 2;
        var height = (int)Math.Ceiling(bounds.Height) + pad * 2;
        using var mask = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(mask))
        {
            graphics.Clear(Color.Transparent);
            // GDI+ fallback remains enabled, including CJK and complex-script shaping. ClearType
            // cannot be used on alpha surfaces because its colored fringe assumes an opaque backdrop.
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var format = TextFormat();
            graphics.DrawString(text, font, Brushes.White, new RectangleF(pad, pad, bounds.Width, bounds.Height), format);
        }
        var data = mask.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var alpha = new byte[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++) alpha[y * width + x] = pixels[y * data.Stride + x * 4 + 3];
            var stroke = outline ? Dilate(alpha, width, height, Math.Max(1, (int)Math.Round((soft ? .7f : 1.4f) * scale))) : alpha;
            var shadow = outline ? Blur(stroke, width, height, Math.Max(1, (int)Math.Ceiling(2 * scale))) : alpha;
            var offset = (int)Math.Ceiling(2 * scale);
            Array.Clear(pixels);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x; var pixel = y * data.Stride + x * 4;
                    var a = 0; var b = 0; var g = 0; var r = 0;
                    if (outline && y >= offset) Over(ref b, ref g, ref r, ref a, Color.Black, shadow[index - offset * width] * (soft ? 90 : 165) / 255);
                    if (outline) Over(ref b, ref g, ref r, ref a, Color.FromArgb(12, 17, 24), stroke[index] * (soft ? 145 : 232) / 255);
                    Over(ref b, ref g, ref r, ref a, color, alpha[index]);
                    pixels[pixel] = (byte)b; pixels[pixel + 1] = (byte)g; pixels[pixel + 2] = (byte)r; pixels[pixel + 3] = (byte)a;
                }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { mask.UnlockBits(data); }
        destination.DrawImageUnscaled(mask, (int)Math.Round(bounds.X) - pad, (int)Math.Round(bounds.Y) - pad);
    }

    private static void Over(ref int b, ref int g, ref int r, ref int a, Color color, int alpha)
    {
        var remaining = 255 - alpha;
        b = (color.B * alpha + b * remaining + 127) / 255;
        g = (color.G * alpha + g * remaining + 127) / 255;
        r = (color.R * alpha + r * remaining + 127) / 255;
        a = alpha + (a * remaining + 127) / 255;
    }

    private static byte[] Dilate(byte[] input, int width, int height, int radius)
    {
        var horizontal = new byte[input.Length]; var output = new byte[input.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                byte max = 0;
                for (var dx = Math.Max(0, x - radius); dx <= Math.Min(width - 1, x + radius); dx++) max = Math.Max(max, input[y * width + dx]);
                horizontal[y * width + x] = max;
            }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                byte max = 0;
                for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius); dy++) max = Math.Max(max, horizontal[dy * width + x]);
                output[y * width + x] = max;
            }
        return output;
    }

    private static byte[] Blur(byte[] input, int width, int height, int radius)
    {
        var horizontal = new byte[input.Length]; var output = new byte[input.Length]; var count = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var sum = 0;
            for (var x = 0; x < Math.Min(width, radius); x++) sum += input[y * width + x];
            for (var x = 0; x < width; x++)
            {
                if (x + radius < width) sum += input[y * width + x + radius];
                if (x - radius - 1 >= 0) sum -= input[y * width + x - radius - 1];
                horizontal[y * width + x] = (byte)(sum / count);
            }
        }
        for (var x = 0; x < width; x++)
        {
            var sum = 0;
            for (var y = 0; y < Math.Min(height, radius); y++) sum += horizontal[y * width + x];
            for (var y = 0; y < height; y++)
            {
                if (y + radius < height) sum += horizontal[(y + radius) * width + x];
                if (y - radius - 1 >= 0) sum -= horizontal[(y - radius - 1) * width + x];
                output[y * width + x] = (byte)(sum / count);
            }
        }
        return output;
    }

    private static GraphicsPath RoundRect(Rectangle bounds, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
