using System.Collections.Immutable;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

// Fixed player controls and battlefield avoidance use the same geometry.
// Separate reservations leave the useful table space between these surfaces.
public sealed record TurnControlLayout(ImmutableArray<Vector2> Markers, Vector2 AutoCenter, Vector2 HoldCenter, Rectangle PriorityArea)
{
    public const int MarkerRadius = 16, ToggleRadius = 16, ManaOffset = 102;
    // Include the saved-pause corner badge and the generous circular hit areas.
    public ImmutableArray<Rectangle> PhaseBounds => Markers.Select(center => Bounds(center, MarkerRadius + 4)).ToImmutableArray();
    public Rectangle ArcBounds => PhaseBounds.Aggregate(Rectangle.Union);
    public Rectangle AutoBounds => Bounds(AutoCenter, ToggleRadius + 2);
    public Rectangle HoldBounds => Bounds(HoldCenter, ToggleRadius + 2);
    public Vector2 AlertCenter => HoldCenter + new Vector2(-21, -42);
    public Rectangle AlertBounds => Bounds(AlertCenter, 14);
    public Rectangle PriorityAction => new(PriorityArea.X, PriorityArea.Bottom - 40, PriorityArea.Width, 40);
    public ImmutableArray<Rectangle> Reservations => [ArcBounds, AutoBounds, HoldBounds, AlertBounds, PriorityArea];

    public static TurnControlLayout For(Vector2 hero)
    {
        var markers = Enumerable.Range(0, 6).Select(index => {
            float angle = (200 + index * 28) * MathF.PI / 180;
            return hero + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 74;
        }).ToImmutableArray();
        return new(markers, hero + new Vector2(-53, 18), hero + new Vector2(53, 18),
            new((int)hero.X + 146, (int)hero.Y - 36, 360, 70));
    }

    private static Rectangle Bounds(Vector2 center, int radius)
    {
        int left = (int)MathF.Floor(center.X - radius), top = (int)MathF.Floor(center.Y - radius);
        return new(left, top, (int)MathF.Ceiling(center.X + radius) - left, (int)MathF.Ceiling(center.Y + radius) - top);
    }
}
