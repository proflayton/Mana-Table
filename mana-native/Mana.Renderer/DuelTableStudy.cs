using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Canonical rows for an actual two-player match.</summary>
public static class DuelTableStudy
{
    public static Rectangle ActionArea => new(1200, 340, 350, 250);
    public static Rectangle MatchLane(int seat, string role) => PlayerPlaymat.Lane(seat, role, duel: true);
}
