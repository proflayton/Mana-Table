using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>
/// A presentation-space camera over the board. This does not change the
/// table layout, seat ownership, or the fixed hand and interaction surfaces.
/// </summary>
public readonly record struct BoardView(Vector2 Offset, float Zoom = 1)
{
    public static BoardView Identity => new(Vector2.Zero);
    // default(BoardView), used by optional presentation arguments, is identity.
    public float Scale => Zoom == 0 ? 1 : Zoom;
    public bool IsIdentity => Offset == Vector2.Zero && Scale == 1;

    public Vector2 Apply(Vector2 point) => point * Scale + Offset;
    public Vector2 Inverse(Vector2 point) => (point - Offset) / Scale;
    public Pose Apply(Pose pose) => pose with { Center = Apply(pose.Center), Size = pose.Size * Scale,
        Perspective = pose.Perspective / Scale, Elevation = pose.Elevation * Scale };
    public Pose Inverse(Pose pose) => pose with { Center = Inverse(pose.Center), Size = pose.Size / Scale,
        Perspective = pose.Perspective * Scale, Elevation = pose.Elevation / Scale };

    // Integer UI anchors use the same rounded displacement in both directions.
    // Card faces and their exact hit shapes retain the subpixel pose above.
    private Point PixelOffset => new((int)MathF.Round(Offset.X), (int)MathF.Round(Offset.Y));
    public Point Apply(Point point) => Scale == 1 ? point + PixelOffset : Round(Apply(point.ToVector2()));
    public Point Inverse(Point point) => Scale == 1 ? point - PixelOffset : Round(Inverse(point.ToVector2()));
    public Rectangle Apply(Rectangle bounds) => Map(bounds, false);
    public Rectangle Inverse(Rectangle bounds) => Map(bounds, true);
    private static Point Round(Vector2 point) => new((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
    private Rectangle Map(Rectangle bounds, bool inverse)
    {
        var end = new Point(bounds.Right, bounds.Bottom);
        var top = inverse ? Inverse(bounds.Location) : Apply(bounds.Location);
        var bottom = inverse ? Inverse(end) : Apply(end);
        return new(top.X, top.Y, bottom.X - top.X, bottom.Y - top.Y);
    }

    public static BoardView Frame(Rectangle world, Rectangle screen, float maximumZoom = 1.8f)
    {
        if (world.Width <= 0 || world.Height <= 0 || screen.Width <= 0 || screen.Height <= 0
            || !float.IsFinite(maximumZoom) || maximumZoom <= 0) throw new ArgumentOutOfRangeException(nameof(world));
        float zoom = Math.Min(maximumZoom, Math.Min(screen.Width / (float)world.Width, screen.Height / (float)world.Height));
        return new(screen.Center.ToVector2() - world.Center.ToVector2() * zoom, zoom);
    }

    // Zone piles and command controls stay anchored with the player resources
    // in this first experiment. Only the battlefield participates in framing.
    public static bool AffectsZone(string zone) => zone == "Battlefield";
}
