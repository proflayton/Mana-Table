using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Table-wide inputs prepared once before arranging individual fields.</summary>
internal sealed class BattlefieldLayoutContext
{
    internal static BattlefieldLayoutContext RoleLanes { get; } = new([]);
    internal IReadOnlyList<Rectangle> Obstacles { get; }
    private readonly IReadOnlyDictionary<int, BattlefieldCluster[]>? roleLanes;

    private BattlefieldLayoutContext(Rectangle[] obstacles, IReadOnlyDictionary<int, BattlefieldCluster[]>? roleLanes = null)
    {
        Obstacles = Array.AsReadOnly(obstacles);
        this.roleLanes = roleLanes;
    }
    internal BattlefieldCluster[]? PreparedRoleLanes(int owner) => roleLanes?.GetValueOrDefault(owner);

    internal static BattlefieldLayoutContext Build(GameSnapshot state, IReadOnlySet<string>? separate,
        IReadOnlyList<SeatGeometry> seats, Func<Card, bool>? isResource = null)
    {
        var obstacles = ReserveObjects(state, seats).ToArray();
        // Unsupported seat geometry retains its established compatibility
        // behavior. Ordinary supported tables prepare their full-size lanes
        // once, before any seat is moved around shared protected objects.
        if (isResource == null || state.Players.Length > 4) return RoleLanes;
        var ordinary = state.Players.Select((player, index) => new RoleLaneSeat(seats[index].Index, player.Id,
            BattlefieldLayout.Arrange(RoleLanes, seats[index].Index, player.Id, player.Zone("Battlefield").Cards,
                isResource, separate))).ToArray();
        return new(obstacles, RoleLaneAvoidance.Arrange(ordinary, obstacles, separate));
    }

    internal static IEnumerable<Rectangle> ReserveObjects(GameSnapshot state, IReadOnlyList<SeatGeometry> seats)
    {
        for (int playerIndex = 0; playerIndex < state.Players.Length; playerIndex++) {
            var player = state.Players[playerIndex];
            var geometry = seats[playerIndex];
            int seat = geometry.Index;
            var hero = geometry.Hero;
            yield return new((int)hero.X - 55, (int)hero.Y - 55, 110, 110);
            if (seat == 3) {
                yield return new((int)hero.X - 130, (int)hero.Y - 55, 190, 110);
                foreach (var reservation in TurnControlLayout.For(hero).Reservations) yield return reservation;
            }
            else yield return new((int)geometry.Hand.X - 83, (int)geometry.Hand.Y - 30, 166, 90);

            foreach (var pile in geometry.Piles) {
                yield return PileGeometry.Create(pile.Pose, player.Zone(pile.Zone).Count, pile.Compact).Reservation;
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
                    int inner = seat == 3 ? TurnControlLayout.ManaOffset : 58;
                    yield return new((int)hero.X - inner - width, (int)hero.Y - 42, width, 84);
                    yield return new((int)hero.X + inner, (int)hero.Y - 42, width, 84);
                }
            }
            if (player.States.Length > 0 || player.Counters.Any(counter => counter.Value > 0))
                yield return seat == 3 ? new((int)hero.X - 390, (int)hero.Y - 24, 190, 76)
                    : seat == 1 ? new((int)hero.X - 98, (int)hero.Y + 35, 150, 66)
                    : new((int)hero.X - 98, (int)hero.Y - 90, 196, 55);
        }
        if (state.Stack.Length > 0 || state.Decision?.Context.Source?.Card != null && !DecisionLayout.UsesSourceWorkspace(state.Decision))
            yield return new(545, 285, 435, state.Stack.Length > 1 ? 400 : 265);
        yield return new(1240, 550, 350, 245);
        if (state.Decision is { Payload: OptionsPayload or AbilityChoicePayload or OrderingPayload }) yield return new(350, 714, 370, 50);
    }
}
