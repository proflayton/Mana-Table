using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Canonical rows for an actual two-player match.</summary>
public static class DuelTableStudy
{
    public static Rectangle ActionArea => new(1200, 340, 350, 250);
    public static Rectangle MatchLane(int seat, string role) => (seat, role) switch {
        (1, "Creatures") => new(300, 245, 880, 220),
        (3, "Creatures") => new(300, 485, 880, 130),
        (3, "Support") => new(195, 500, 100, 140),
        (3, _) => new(330, 625, 270, 100),
        (1, "Support") => new(300, 145, 530, 90),
        (1, _) => new(845, 145, 335, 90),
        _ => Rectangle.Empty
    };
}
