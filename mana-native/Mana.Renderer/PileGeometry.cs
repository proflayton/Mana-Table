using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>The displayed pile, count and label share one geometry with battlefield reservations.</summary>
public sealed record PileGeometry(Pose Shadow, IReadOnlyList<Pose> Layers, Pose Top,
    Rectangle Label, Rectangle Count, Vector2 CountCenter, Rectangle HitBounds, Rectangle Reservation)
{
    public bool Contains(Point point) => Top.Contains(point) || Label.Contains(point) || Count.Contains(point);

    public static PileGeometry Create(Pose pose, int count, bool compact)
    {
        int thickness = count == 0 ? 0 : Math.Clamp(count / 12, 1, 7);
        var shadow = pose with { Center = pose.Center + new Vector2(4, 6), Size = pose.Size + new Vector2(4) };
        var layers = Enumerable.Range(0, thickness).Select(index => pose with {
            Center = pose.Center - new Vector2(0, index * 1.5f)
        }).ToArray();
        var top = pose with { Center = pose.Center - new Vector2(0, thickness * 1.5f) };
        var bounds = top.Bounds;
        var label = compact ? new Rectangle((int)top.Center.X - 42, bounds.Bottom, 84, 19)
            : new Rectangle((int)top.Center.X - 38, bounds.Bottom + 17, 76, 18);
        var countBounds = compact ? Rectangle.Empty
            : new Rectangle((int)top.Center.X - 15, bounds.Bottom - 10, 30, 24);
        var hitBounds = Rectangle.Union(bounds, label);
        if (!countBounds.IsEmpty) hitBounds = Rectangle.Union(hitBounds, countBounds);
        var reservation = Rectangle.Union(Rectangle.Union(hitBounds, pose.Bounds), shadow.Bounds);
        // Preserve the existing clearance around the rendered objects. Its
        // base now includes the actual label width and displaced top face.
        reservation.Inflate(7, 7);
        return new(shadow, Array.AsReadOnly(layers), top, label, countBounds,
            new(top.Center.X, bounds.Bottom + 2), hitBounds, reservation);
    }
}
