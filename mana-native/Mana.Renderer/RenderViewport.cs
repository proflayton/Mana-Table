using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>One mapping between logical composition, fitted target pixels and window input.</summary>
public readonly record struct RenderViewport(Rectangle Bounds, Vector2 Scale)
{
    public static RenderViewport ForSurface(Point pixels, int designHeight) => new(
        new(0, 0, Math.Max(1, pixels.X), Math.Max(1, pixels.Y)),
        new(Math.Max(1, pixels.X) / 1600f, Math.Max(1, pixels.Y) / (float)Math.Max(1, designHeight)));

    public static RenderViewport Fit(Point pixels, int designHeight)
    {
        int width = Math.Max(1, pixels.X), height = Math.Max(1, pixels.Y);
        designHeight = Math.Max(1, designHeight);
        float fit = Math.Min(width / 1600f, height / (float)designHeight);
        int fittedWidth = Math.Clamp((int)MathF.Round(1600 * fit), 1, width);
        int fittedHeight = Math.Clamp((int)MathF.Round(designHeight * fit), 1, height);
        return new(new((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight),
            new(fittedWidth / 1600f, fittedHeight / (float)designHeight));
    }

    public Point ToLogical(Point physical) => new(
        (int)MathF.Floor((physical.X - Bounds.X) / Scale.X),
        (int)MathF.Floor((physical.Y - Bounds.Y) / Scale.Y));

    public Vector2 ToPhysical(Vector2 logical) => Bounds.Location.ToVector2() + logical * Scale;

    public Rectangle ToSurface(Rectangle logical)
    {
        int left = (int)MathF.Floor(logical.Left * Scale.X), top = (int)MathF.Floor(logical.Top * Scale.Y);
        return new(left, top, (int)MathF.Ceiling(logical.Right * Scale.X) - left,
            (int)MathF.Ceiling(logical.Bottom * Scale.Y) - top);
    }
}
