using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ListeningSubtitles.Core;

namespace ListeningSubtitles.UI;

/// <summary>Produces the premultiplied-alpha surface used both on screen and by the offline preview.</summary>
internal static class LyricRenderer
{
    internal sealed record Frame(Bitmap Image, Rectangle Toggle, Rectangle Menu, Rectangle Handle);

    internal static Frame Render(Size size, float scale, string original, string translation, string live,
        string status, bool active, bool busy, int fontSize, bool toggleHot = false, bool menuHot = false, string previous = "")
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
        var pillHeight = (int)Math.Ceiling(38 * scale);
        var pillY = size.Height - pillHeight - (int)Math.Ceiling(9 * scale);
        var hasText = !string.IsNullOrWhiteSpace(original) || !string.IsNullOrWhiteSpace(translation);
        var main = string.IsNullOrWhiteSpace(original)
            ? (hasText || !string.IsNullOrWhiteSpace(live) ? "" : AppText.T("播放音频后，点击开始", "Play audio, then select Start"))
            : original;
        using var liveFont = FontAt(Math.Clamp(originalPixels * .48f, 13 * scale, 19 * scale), FontStyle.Regular);
        using var statusFont = FontAt(12 * scale, FontStyle.Regular);
        var gap = 6 * scale;
        var textTop = 6 * scale;
        var textBottom = Math.Max(textTop, pillY - 10 * scale);
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
        var y = Math.Max(textTop, textBottom - total);
        var textState = graphics.Save();
        graphics.SetClip(new RectangleF(0, 0, size.Width, Math.Max(0, pillY - 8 * scale)));
        foreach (var row in rows)
        {
            DrawOutlinedText(graphics, row.Text, row.Font, new RectangleF(margin, y, contentWidth, row.Height), row.Color, scale, row.Lines, true, row.Soft);
            y += row.Height + gap;
        }
        graphics.Restore(textState);

        using var chromeFont = FontAt(13 * scale, FontStyle.Regular);
        var action = busy ? AppText.T("请稍候", "Please wait") : active ? AppText.T("停止", "Stop") : AppText.T("开始", "Start");
        var labelWidth = graphics.MeasureString(action, chromeFont).Width;
        var handleWidth = (int)Math.Ceiling(30 * scale);
        var toggleWidth = (int)Math.Ceiling(labelWidth + 43 * scale);
        var menuWidth = (int)Math.Ceiling(38 * scale);
        var pillWidth = handleWidth + toggleWidth + menuWidth;
        var pill = new Rectangle((size.Width - pillWidth) / 2, pillY, pillWidth, pillHeight);
        var handle = new Rectangle(pill.X, pill.Y, handleWidth, pill.Height);
        var toggle = new Rectangle(handle.Right, pill.Y, toggleWidth, pill.Height);
        var menu = new Rectangle(toggle.Right, pill.Y, menuWidth, pill.Height);
        using (var path = RoundRect(pill, 18 * scale))
        {
            using var fill = new SolidBrush(Color.FromArgb(220, 25, 30, 38));
            using var border = new Pen(Color.FromArgb(100, 158, 174, 190), Math.Max(1, scale));
            graphics.FillPath(fill, path); graphics.DrawPath(border, path);
        }
        if (toggleHot && !busy)
        {
            using var hot = new SolidBrush(Color.FromArgb(34, 223, 238, 247));
            graphics.FillRectangle(hot, Rectangle.Inflate(toggle, -1, -4));
        }
        if (menuHot)
        {
            using var hot = new SolidBrush(Color.FromArgb(34, 223, 238, 247));
            using var path = RoundRect(Rectangle.Inflate(menu, -3, -4), 10 * scale);
            graphics.FillPath(hot, path);
        }
        using (var separator = new Pen(Color.FromArgb(55, 175, 188, 204), Math.Max(1, scale)))
            graphics.DrawLine(separator, menu.Left, pill.Top + 10 * scale, menu.Left, pill.Bottom - 10 * scale);
        using (var dot = new SolidBrush(Color.FromArgb(150, 187, 199, 212)))
            for (var column = 0; column < 2; column++)
                for (var row = 0; row < 3; row++)
                    graphics.FillEllipse(dot, handle.Left + (11 + 5 * column) * scale, handle.Top + (12 + 5 * row) * scale, 1.7f * scale, 1.7f * scale);
        var centerY = toggle.Top + toggle.Height / 2f;
        var iconX = toggle.Left + 14 * scale;
        using (var icon = new SolidBrush(busy ? Color.FromArgb(165, 183, 198) : active ? Color.FromArgb(247, 180, 170) : Color.FromArgb(174, 239, 216)))
        {
            if (busy)
            {
                using var pen = new Pen(Color.FromArgb(170, 209, 220, 230), 1.8f * scale);
                graphics.DrawArc(pen, iconX - 4 * scale, centerY - 5 * scale, 10 * scale, 10 * scale, -70, 275);
            }
            else if (active) graphics.FillRectangle(icon, iconX - 3 * scale, centerY - 4 * scale, 8 * scale, 8 * scale);
            else graphics.FillPolygon(icon, new PointF[] { new(iconX - 3 * scale, centerY - 5 * scale), new(iconX - 3 * scale, centerY + 5 * scale), new(iconX + 5 * scale, centerY) });
        }
        DrawOutlinedText(graphics, action, chromeFont, new RectangleF(toggle.Left + 28 * scale, toggle.Top + 1 * scale, toggle.Width - 34 * scale, toggle.Height - 2 * scale),
            busy ? Color.FromArgb(182, 193, 207) : Color.FromArgb(241, 245, 250), scale, 1, false);
        using (var dot = new SolidBrush(Color.FromArgb(235, 238, 244, 250)))
            for (var i = -1; i <= 1; i++) graphics.FillEllipse(dot, menu.Left + menu.Width / 2f + i * 5 * scale - scale, centerY - scale, 2 * scale, 2 * scale);
        return new(bitmap, toggle, menu, handle);
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
        var path = new GraphicsPath(); var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
