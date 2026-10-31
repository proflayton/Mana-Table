using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Stable physical cloth and card rows in the shared tabletop's canonical
/// coordinates. Camera movement transforms this geometry together with its cards;
/// density, turn ownership and pending decisions never resize the material.</summary>
public static class PlayerPlaymat
{
    public const int HemInset = 12, MinimumGap = 16;

    public static Rectangle Bounds(int seat, bool duel = false) => (duel, seat) switch {
        (true, 1) => new(288, 127, 904, 344),
        (true, 3) => new(183, 487, 1009, 250),
        (false, 0) => new(160, 54, 320, 473),
        (false, 1) => new(496, 159, 608, 228),
        (false, 2) => new(1120, 54, 320, 473),
        (false, 3) => new(308, 543, 939, 179),
        _ => throw new ArgumentOutOfRangeException(nameof(seat))
    };

    public static float Facing(int seat, bool duel = false) => duel ? 0 : seat switch {
        0 => MathF.PI / 2, 1 => MathF.PI, 2 => -MathF.PI / 2, _ => 0
    };

    public static Rectangle Lane(int seat, string role, bool duel = false) => (duel, seat, role) switch {
        (true, 1, "Creatures") => new(300, 239, 880, 220),
        (true, 3, "Creatures") => new(300, 499, 880, 130),
        (true, 3, "Support") => new(195, 500, 100, 140),
        (true, 3, _) => new(330, 625, 270, 100),
        (true, 1, "Support") => new(300, 139, 530, 90),
        (true, 1, _) => new(845, 139, 335, 90),
        (true, _, _) => Rectangle.Empty,
        (false, 0, "Creatures") => new(273, 130, 195, 385),
        (false, 0, "Support") => new(172, 66, 94, 210),
        (false, 0, _) => new(172, 285, 94, 230),
        (false, 1, "Creatures") => new(510, 270, 580, 105),
        (false, 1, "Support") => new(510, 171, 430, 94),
        (false, 1, _) => new(955, 171, 135, 94),
        (false, 2, "Creatures") => new(1132, 160, 190, 355),
        (false, 2, "Support") => new(1334, 66, 94, 218),
        (false, 2, _) => new(1334, 393, 94, 122),
        (false, 3, "Creatures") => new(590, 555, 645, 84),
        (false, 3, "Support") => new(320, 555, 260, 155),
        (false, 3, _) => new(980, 640, 255, 64),
        _ => throw new ArgumentOutOfRangeException(nameof(seat))
    };
}
