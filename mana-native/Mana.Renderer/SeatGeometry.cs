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

    private SeatGeometry(Player player, int seat, bool study)
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
        if (study) {
            CommandLink = own ? new(260, 720) : seat == 1 ? new(1180, 174) : new(seat == 0 ? 125 : 1490, 513);
            Commanders = Array.AsReadOnly(leaders.Select((card, index) => {
                var center = own ? new Vector2(230 + index * 60, 682) : seat == 1 ? new Vector2(1140 + index * 80, 118)
                    : new Vector2((seat == 0 ? 75 : 1450) + index * 75, 462);
                return SceneCard.Create(card, new(center, own ? new(48, 67) : new(58, 81)), "Command", player.Id, index, Hero);
            }).ToArray());
            Piles = Array.AsReadOnly(new[] { "Library", "Graveyard", "Exile" }.Select((zone, index) => {
                var center = own ? new Vector2(42 + index * 60, 736) : seat == 1 ? new Vector2(1320 + index * 70, 110)
                    : new Vector2((seat == 0 ? 42 : 1400) + index * (seat == 0 ? 56 : 65), 565);
                return new PilePlan(zone, new(center, new(39, 55)), true);
            }).ToArray());
        }
    }

    internal static SeatGeometry[] Build(GameSnapshot state, bool study = false) => state.Players.Select(player => new SeatGeometry(player,
        player.Id == state.ViewerId ? 3 : TableLayout.SeatIndex(state, player.Id), study)).ToArray();
}
