using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Composes drawing data and measures text without touching a graphics device.</summary>
public sealed class DrawList : IDisposable
{
    private readonly List<DrawCommand> commands = [];
    private readonly TextMetrics metrics = new();
    private bool composing;
    private int clipDepth;
    private float scale = 1;
    private float textScale = 1;
    public float TextScale {
        get => textScale;
        set {
            if (composing) throw new InvalidOperationException("Set text scale before composing a frame.");
            textScale = value;
        }
    }

    public void BeginComposition(float scale = 1)
    {
        if (composing) throw new InvalidOperationException("Finish the current composition before starting another frame.");
        commands.Clear(); clipDepth = 0; this.scale = scale; composing = true;
    }
    public DrawingFrame Complete()
    {
        if (!composing) throw new InvalidOperationException("No frame is being composed.");
        if (clipDepth != 0) throw new InvalidOperationException("A drawing clip was not closed.");
        composing = false;
        metrics.Trim();
        return new(commands, TextScale, scale);
    }
    private void Add(DrawCommand command)
    {
        if (!composing) throw new InvalidOperationException("Drawing data can only be added while composing a frame.");
        commands.Add(command);
    }
    public void BeginClip(Rectangle bounds) { Add(new DrawCommand.BeginClip(bounds)); clipDepth++; }
    public void EndClip()
    {
        if (clipDepth == 0) throw new InvalidOperationException("No drawing clip is open.");
        Add(new DrawCommand.EndClip()); clipDepth--;
    }
    public int TextWidth(string value, float size = 1, bool bold = false) => metrics.TextWidth(value, size, bold, TextScale);
    public Point TextSize(string value, float size, int width, int maxLines, bool bold = false) => metrics.TextSize(value, size, width, maxLines, bold, TextScale);
    public string[] TextPages(string value, float size, int width, int height, int maxLines, bool bold = false) => TextPagination.Pages(value,
        text => TextSize(text, size, width, 80, bold).Y <= Math.Min(height, TextSize(text, size, width, maxLines, bold).Y));
    public void Fill(Rectangle bounds, Color color) => Add(new DrawCommand.Fill(bounds, color));
    public void Rounded(Rectangle bounds, Color color, int radius = 10) => Add(new DrawCommand.Rounded(bounds, color, radius));
    public void Border(Rectangle bounds, Color color, int width = 1) => Add(new DrawCommand.Border(bounds, color, width));
    public void Panel(Rectangle bounds, Color fill, Color trim, int radius = 10) => Add(new DrawCommand.Panel(bounds, fill, trim, radius));
    public void Gradient(Rectangle bounds, Color top, Color bottom) => Add(new DrawCommand.Gradient(bounds, top, bottom));
    public void Circle(Vector2 center, float radius, Color color) => Add(new DrawCommand.Circle(center, radius, color));
    public void Glow(Vector2 center, Vector2 size, Color color) => Add(new DrawCommand.Glow(center, size, color));
    public void Ring(Vector2 center, float radius, Color color, float width = 2, float start = 0, float sweep = MathF.PI * 2) => Add(new DrawCommand.Ring(center, radius, color, width, start, sweep));
    public void Line(Vector2 from, Vector2 to, Color color, float width = 2) => Add(new DrawCommand.Line(from, to, color, width));
    public void Arrow(Vector2 from, Vector2 to, Color color, float width = 10) => Add(new DrawCommand.Arrow(from, to, color, width));
    public void Image(Texture2D texture, Rectangle bounds, float alpha = 1) => Add(new DrawCommand.Image(texture, bounds, alpha));
    public void Sprite(Texture2D texture, Vector2 center, Vector2 size, float rotation = 0, Color? tint = null, Rectangle? crop = null) => Add(new DrawCommand.Sprite(texture, center, size, rotation, tint, crop));
    public void Quad(Vector2 center, Vector2 size, float angle, Color tint) => Add(new DrawCommand.Quad(center, size, angle, tint));
    public void Surface(Texture2D? texture, Pose pose, Color tint) => Add(new DrawCommand.Surface(texture, pose, tint));
    public void Polygon(IReadOnlyList<Vector2> points, Color color) => Add(new DrawCommand.Polygon(Array.AsReadOnly(points.ToArray()), color));
    public void Portrait(Texture2D texture, Vector2 center, float radius, Color tint) => Add(new DrawCommand.Portrait(texture, center, radius, tint));
    public void ProjectedEllipse(Texture2D texture, Pose pose, Color tint) => Add(new DrawCommand.ProjectedEllipse(texture, pose, tint));
    public void CardFrame(Pose pose, Color color, int width = 2) => Add(new DrawCommand.CardFrame(pose, color, width));
    public void CardFrame(Vector2 center, Vector2 size, float angle, Color color, int width = 2) => Add(new DrawCommand.FlatCardFrame(center, size, angle, color, width));
    public void CardFocus(Pose pose, Color accent) => Add(new DrawCommand.CardFocus(pose, accent));
    public void CardAction(Pose pose, Color accent, float glow = 1) => Add(new DrawCommand.CardAction(pose, accent, glow));
    public void Text(string? value, int x, int y, Color color, float size = 1, int width = 2000, int maxLines = 100, bool bold = false, bool serif = false) => Add(new DrawCommand.Text(value, x, y, color, size, width, maxLines, bold, serif));
    public void CenterText(string? value, Rectangle bounds, Color color, float size = 1, bool bold = false, bool serif = false) => Add(new DrawCommand.CenterText(value, bounds, color, size, bold, serif));
    public void Dispose() => metrics.Clear();
}
