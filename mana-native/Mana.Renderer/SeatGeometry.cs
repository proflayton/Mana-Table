using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Renderer;

/// <summary>Seat objects shared by the scene and battlefield space reservations.</summary>
public sealed class SeatGeometry
{
    public int PlayerId { get; }
    public int Index { get; }
    public Vector2 Hero { get; }
    public Vector2 Hand { get; }
    public Vector2 CommandLink { get; }
    public IReadOnlyList<SceneCard> Commanders { get; }
    public IReadOnlyList<PilePlan> Piles { get; }

    private SeatGeometry(Player player, int seat)
    {
        PlayerId = player.Id;
        Index = seat;
        Hero = TableLayout.Hero(seat);
        Hand = TableLayout.SeatHand(seat);
        bool own = seat == 3, side = seat is 0 or 2;
        CommandLink = own ? new(191, 647) : seat == 2 ? new(1380, 520)
            : side ? TableWorld.Project(TableWorld.SeatPoint(seat, -270, 645)) : new(1100, 156);
        var leaders = player.Zone("Command").Cards.Take(2).ToArray();
        Commanders = Array.AsReadOnly(leaders.Select((card, index) => {
            Vector2 center = own ? new(-516 + index * 82, 202)
                : side ? TableWorld.SeatPoint(seat, -262 + index * 91, 525)
                : TableWorld.SeatPoint(seat, -359 - index * 85, 302);
            var size = own ? leaders.Length > 1 ? new Vector2(62, 87) : new Vector2(81, 114) : new Vector2(69, 97);
            return SceneCard.Create(card, TableWorld.Card(center, size, TableWorld.SeatAngle(seat)), "Command", player.Id, index, Hero);
        }).ToArray());
        Piles = Array.AsReadOnly(new[] { "Library", "Graveyard", "Exile" }
            .Select((zone, index) => new PilePlan(zone, TableWorld.Pile(seat, index), side)).ToArray());
    }

    internal static SeatGeometry[] Build(GameSnapshot state) => state.Players.Select(player => new SeatGeometry(player,
        player.Id == state.ViewerId ? 3 : TableLayout.SeatIndex(state, player.Id))).ToArray();
}
