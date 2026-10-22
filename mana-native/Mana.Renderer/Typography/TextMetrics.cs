using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Gdi = System.Drawing;

namespace Mana.Renderer;

internal readonly record struct TextLayout(string Value, int Size, int Width, int Lines, bool Bold, bool Serif)
{
    private static float Scale(float scale) => float.IsFinite(scale) ? Math.Clamp(scale, 1, 1.3f) : 1;
    public static TextLayout WidthOf(string value, float size, bool bold, float scale) => new(value, Math.Max(9, (int)Math.Round(22 * size * Scale(scale))), 2000, 1, bold, false);
    public static TextLayout Block(string value, float size, int width, int lines, bool bold, bool serif, float scale) => new(value.Replace("\r", ""), Math.Max(9, (int)Math.Round(22 * size * Scale(scale))), Math.Min(2000, width), Math.Max(1, (int)(Math.Min(80, lines) / Scale(scale))), bold, serif);
    public static TextLayout Centered(string value, float size, Rectangle bounds, bool bold, bool serif, float scale) => new(value, Math.Max(9, Math.Min((int)Math.Round(22 * size * Scale(scale)), (int)(bounds.Height * .76f))), bounds.Width, 1, bold, serif);
}

/// <summary>CPU text geometry shared by composition and GPU rasterization.</summary>
public sealed class TextMetrics
{
    private readonly Dictionary<TextLayout, Point> measurements = [];
    internal Point Measure(TextLayout layout)
    {
        if (!measurements.TryGetValue(layout, out var size)) measurements[layout] = size = TextRasterizer.Measure(layout);
        return size;
    }
    public int TextWidth(string value, float size = 1, bool bold = false, float textScale = 1) => value.Length == 0 ? 0 : Measure(TextLayout.WidthOf(value, size, bold, textScale)).X;
    public Point TextSize(string value, float size, int width, int maxLines, bool bold = false, float textScale = 1) => value.Length == 0 ? Point.Zero : Measure(TextLayout.Block(value, size, width, maxLines, bold, false, textScale));
    internal void Trim()
    {
        if (measurements.Count > 2048) foreach (var key in measurements.Keys.Take(measurements.Count - 1024).ToArray()) measurements.Remove(key);
    }
    internal void Clear() => measurements.Clear();
}

internal static class TextRasterizer
{
    private static Gdi.Font Font(TextLayout layout) => new(layout.Serif ? "Georgia" : "Segoe UI", layout.Size, layout.Bold ? Gdi.FontStyle.Bold : Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
    private static Gdi.StringFormat Format() => new(Gdi.StringFormat.GenericTypographic) { Trimming = Gdi.StringTrimming.EllipsisWord };
    internal static Point Measure(TextLayout layout)
    {
        using var font = Font(layout);
        using var bitmap = new Gdi.Bitmap(1, 1);
        using var graphics = Gdi.Graphics.FromImage(bitmap);
        using var format = Format();
        var measured = graphics.MeasureString(layout.Value, font, layout.Width, format);
        int height = Math.Max(1, Math.Min((int)Math.Ceiling(measured.Height) + 4, (int)Math.Ceiling(font.GetHeight(graphics) * layout.Lines) + 2));
        int width = Math.Max(1, Math.Min(layout.Width, (int)Math.Ceiling(measured.Width) + 6));
        return new(width, height);
    }
    internal static Gdi.Bitmap Rasterize(TextLayout layout, Point size) => Rasterize(layout, size, Vector2.One);
    internal static Gdi.Bitmap Rasterize(TextLayout layout, Point size, Vector2 density)
    {
        if (!float.IsFinite(density.X) || !float.IsFinite(density.Y) || density.X <= 0 || density.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(density));
        using var font = Font(layout);
        using var format = Format();
        var bitmap = new Gdi.Bitmap(Math.Max(1, checked((int)Math.Ceiling(size.X * (double)density.X))),
            Math.Max(1, checked((int)Math.Ceiling(size.Y * (double)density.Y))), Gdi.Imaging.PixelFormat.Format32bppArgb);
        try {
            using var graphics = Gdi.Graphics.FromImage(bitmap);
            graphics.Clear(Gdi.Color.Transparent);
            // Keep font selection and wrapping in logical units; only glyph
            // coverage is rasterized at the destination's actual pixel density.
            graphics.ScaleTransform(density.X, density.Y);
            graphics.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(layout.Value, font, Gdi.Brushes.White, new Gdi.RectangleF(0, 0, size.X, size.Y), format);
            return bitmap;
        } catch { bitmap.Dispose(); throw; }
    }
}
