using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Table-wide inputs prepared once before arranging individual fields.</summary>
internal sealed class BattlefieldLayoutContext
{
    internal static BattlefieldLayoutContext RoleLanes { get; } = new(false, []);
    internal bool Bounded { get; }
    internal IReadOnlyList<Rectangle> Obstacles { get; }

    private BattlefieldLayoutContext(bool bounded, Rectangle[] obstacles)
    {
        Bounded = bounded;
        Obstacles = Array.AsReadOnly(obstacles);
    }

    internal static BattlefieldLayoutContext Build(GameSnapshot state, IReadOnlySet<string>? separate,
        IReadOnlyList<SeatGeometry> seats)
    {
        // This is a coordinated table mode. Even seats outside the four-seat
        // viewport keep their existing influence on mode and reserved space.
        bool bounded = state.Players.Any(player => BattlefieldLayout.Groups(player.Zone("Battlefield").Cards, separate, 8).Length > 7);
        return bounded ? new(true, ReserveObjects(state, seats).ToArray()) : RoleLanes;
    }

    private static IEnumerable<Rectangle> ReserveObjects(GameSnapshot state, IReadOnlyList<SeatGeometry> seats)
    {
        for (int playerIndex = 0; playerIndex < state.Players.Length; playerIndex++) {
            var player = state.Players[playerIndex];
            var geometry = seats[playerIndex];
            int seat = geometry.Index;
            var hero = geometry.Hero;
            yield return new((int)hero.X - 55, (int)hero.Y - 55, 110, 110);
            if (seat == 3) yield return new((int)hero.X - 130, (int)hero.Y - 55, 190, 110);
            else yield return new((int)geometry.Hand.X - 83, (int)geometry.Hand.Y - 30, 166, 90);

            foreach (var pile in geometry.Piles) {
                var bounds = pile.Pose.Bounds;
                bounds.Inflate(7, 7);
                bounds.Height += 40;
                yield return bounds;
            }
            var command = geometry.CommandLink;
            yield return new((int)command.X - 56, (int)command.Y - 3, 112,
                player.Zone("Command").Count > 0 ? 52 : 30);
            foreach (var commander in geometry.Commanders) {
                var bounds = commander.Pose.Bounds;
                bounds.Inflate(7, 12);
                yield return bounds;
            }

            var positive = player.Mana.Where(part => part.Value > 0).ToArray();
            if (positive.Length > 0) {
                int width = Math.Max(34, positive.Max(part => 30 + part.Value.ToString().Length * 11));
                if (seat is 0 or 2) yield return new(seat == 0 ? (int)hero.X + 50 : (int)hero.X - 50 - width * 2 - 6, (int)hero.Y - 42, width * 2 + 6, 84);
                else {
                    yield return new((int)hero.X - 58 - width, (int)hero.Y - 42, width, 84);
                    yield return new((int)hero.X + 58, (int)hero.Y - 42, width, 84);
                }
            }
            if (player.States.Length > 0 || player.Counters.Any(counter => counter.Value > 0))
                yield return seat == 3 ? new((int)hero.X - 390, (int)hero.Y - 24, 190, 76)
                    : seat == 1 ? new((int)hero.X - 98, (int)hero.Y + 35, 150, 66)
                    : new((int)hero.X - 98, (int)hero.Y - 90, 196, 55);
        }
        if (state.Stack.Length > 0 || state.Decision?.SourceCard != null) yield return new(545, 285, 435, 265);
        yield return new(1240, 550, 350, 245);
        if (state.Decision is { Kind: "choice" }) yield return new(350, 714, 370, 50);
    }
}
