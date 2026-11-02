using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>One physical mat and its owner-relative card rows. A seat places this
/// template with a quarter turn and translation; density, turn ownership and
/// pending decisions never change its size or proportions.</summary>
public static class PlayerPlaymat
{
    public const int HemInset = 12, MinimumGap = 32;
    public static Rectangle CanonicalBounds => new(0, 0, 512, 288);
    // A fixed physical perimeter supports the mats and seat-side personal rows.
    // Portraits and held hands remain fixed seat HUD objects.
    public static Rectangle TabletopPlacement => new(72, 136, 1456, 708);
    public static Rectangle PersonalArea => new(162, 144, 188, 132);
    public static Rectangle CanonicalCounterBand => new(12, 216, 150, 60);

    public static Rectangle Bounds(int seat, bool duel = false) => Project(seat, CanonicalBounds, duel);
    public static Rectangle Bounds(TableSeatFrame seat) => seat.Project(CanonicalBounds);

    // A duel retains the established viewer-upright card faces. The mat and its
    // owner-relative role positions still use the same physical seat frame.
    public static float Facing(int seat, bool duel = false) => duel ? 0 : TableSeatFrame.For(seat, study: false).Facing;

    public static Rectangle CanonicalLane(string role) => role switch {
        "Support" => new(12, 12, 150, 192),
        "Creatures" => new(174, 12, 326, 132),
        _ => new(350, 174, 150, 102)
    };

    public static Rectangle Lane(int seat, string role, bool duel = false) =>
        duel && seat is not (1 or 3) ? Rectangle.Empty : Project(seat, CanonicalLane(role), duel);
    public static Rectangle Lane(TableSeatFrame seat, string role) => seat.Project(CanonicalLane(role));

    public static Rectangle Project(int seat, Rectangle local, bool duel = false) =>
        TableSeatFrame.For(seat, study: false, duel: duel).Project(local);
}
