using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Explicit two-field framing of a multiplayer table; player identity stays unchanged.</summary>
public static class DuelTableStudy
{
    public static Rectangle ActionArea => new(1200, 340, 350, 250);
    // A complete two-player table gives the local field the central row too.
    // Multiplayer focus retains its smaller local row and other-seat rail.
    public static Rectangle MatchLane(int seat, string role) => (seat, role) switch {
        (1, "Creatures") => new(300, 245, 880, 220),
        (3, "Creatures") => new(300, 485, 880, 130),
        (3, "Support") => new(195, 500, 100, 140),
        (3, _) => new(330, 625, 270, 100),
        _ => Lane(seat, role)
    };
    public static Rectangle Lane(int seat, string role) => (seat, role) switch {
        (1, "Creatures") => new(300, 245, 880, 285),
        (1, "Support") => new(300, 145, 530, 90),
        (1, _) => new(845, 145, 335, 90),
        (3, "Creatures") => new(540, 555, 640, 84),
        (3, "Support") => new(330, 555, 200, 155),
        (3, _) => new(980, 646, 255, 55),
        _ => Rectangle.Empty
    };
}
