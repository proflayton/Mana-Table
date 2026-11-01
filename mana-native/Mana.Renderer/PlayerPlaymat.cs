using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>One physical mat and its owner-relative card rows. A seat places this
/// template with a quarter turn and translation; density, turn ownership and
/// pending decisions never change its size or proportions.</summary>
public static class PlayerPlaymat
{
    public const int HemInset = 12, MinimumGap = 32;
    public static Rectangle CanonicalBounds => new(0, 0, 512, 288);
    // A fixed physical perimeter: close to the far/near mats, with only the
    // side apron required by the existing command and public-pile rails. The
    // far player's portrait, held hand and zone controls remain fixed seat HUD.
    public static Rectangle TabletopPlacement => new(72, 136, 1456, 708);
    public static Rectangle PersonalArea => new(162, 144, 188, 132);
    public static Rectangle CanonicalCounterBand => new(12, 216, 150, 60);

    public static Rectangle Bounds(int seat, bool duel = false) => Project(seat, CanonicalBounds, duel);

    // A duel retains the established viewer-upright card faces. The mat and its
    // owner-relative role positions still use the same physical seat frame.
    public static float Facing(int seat, bool duel = false) => duel ? 0 : seat switch {
        0 => MathF.PI / 2, 1 => MathF.PI, 2 => -MathF.PI / 2, _ => 0
    };

    public static Rectangle CanonicalLane(string role) => role switch {
        "Support" => new(12, 12, 150, 192),
        "Creatures" => new(174, 12, 326, 132),
        _ => new(350, 174, 150, 102)
    };

    public static Rectangle Lane(int seat, string role, bool duel = false) =>
        duel && seat is not (1 or 3) ? Rectangle.Empty : Project(seat, CanonicalLane(role), duel);

    public static Rectangle Project(int seat, Rectangle local, bool duel = false)
    {
        if (duel && seat is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(seat));
        var center = seat switch {
            0 => new Point(368, 448),
            1 => new Point(800, 304),
            2 => new Point(1232, 448),
            3 => new Point(800, 660),
            _ => throw new ArgumentOutOfRangeException(nameof(seat))
        };
        var origin = CanonicalBounds.Center;
        int left = local.Left - origin.X, right = local.Right - origin.X;
        int top = local.Top - origin.Y, bottom = local.Bottom - origin.Y;
        // Exact integer quarter turns retain the same physical area and hem;
        // independently rounded corners cannot stretch one player's material.
        return seat switch {
            0 => new(center.X - bottom, center.Y + left, local.Height, local.Width),
            1 => new(center.X - right, center.Y - bottom, local.Width, local.Height),
            2 => new(center.X + top, center.Y - right, local.Height, local.Width),
            _ => new(center.X + left, center.Y + top, local.Width, local.Height)
        };
    }
}
