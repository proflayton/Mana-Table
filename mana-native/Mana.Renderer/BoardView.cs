using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>
/// A presentation-space translation of the board. This does not change the
/// table layout, seat ownership, or the fixed hand and interaction surfaces.
/// </summary>
public readonly record struct BoardView(Vector2 Offset)
{
    public static BoardView Identity => new(Vector2.Zero);
    public bool IsIdentity => Offset == Vector2.Zero;

    public Vector2 Apply(Vector2 point) => point + Offset;
    public Vector2 Inverse(Vector2 point) => point - Offset;
    public Pose Apply(Pose pose) => pose with { Center = Apply(pose.Center) };
    public Pose Inverse(Pose pose) => pose with { Center = Inverse(pose.Center) };

    // Integer UI anchors use the same rounded displacement in both directions.
    // Card faces and their exact hit shapes retain the subpixel pose above.
    private Point PixelOffset => new((int)MathF.Round(Offset.X), (int)MathF.Round(Offset.Y));
    public Point Apply(Point point) => point + PixelOffset;
    public Point Inverse(Point point) => point - PixelOffset;
    public Rectangle Apply(Rectangle bounds) => bounds with { X = bounds.X + PixelOffset.X, Y = bounds.Y + PixelOffset.Y };
    public Rectangle Inverse(Rectangle bounds) => bounds with { X = bounds.X - PixelOffset.X, Y = bounds.Y - PixelOffset.Y };

    // Zone piles and command controls stay anchored with the player resources
    // in this first experiment. Only the battlefield participates in framing.
    public static bool AffectsZone(string zone) => zone == "Battlefield";
}
