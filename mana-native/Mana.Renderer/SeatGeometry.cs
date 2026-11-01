using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Seat objects shared by the scene and battlefield space reservations.</summary>
public sealed class SeatGeometry
{
    public int PlayerId { get; }
    public int Index { get; }
    public Rectangle? PlaymatBounds { get; }
    public Rectangle? PlaymatPlacement { get; }
    public Pose? PlaymatSurface { get; }
    public Rectangle? PublicCounterArea { get; }
    public Vector2 Hero { get; }
    public Vector2 Hand { get; }
    public TurnControlLayout TurnControls { get; }
    public Vector2 CommandLink { get; }
    public IReadOnlyList<SceneCard> Commanders { get; }
    public IReadOnlyList<PilePlan> Piles { get; }
    public SeatZoneLayout? ZoneStation { get; }
    public Rectangle? LandReadoutArea { get; }

    private SeatGeometry(Player player, int seat, bool study, bool duel = false)
    {
        PlayerId = player.Id;
        Index = seat;
        PlaymatPlacement = study ? PlayerPlaymat.Bounds(seat, duel) : null;
        PlaymatSurface = PlaymatPlacement is { } placement ? TableSurfaceProjection.Surface(placement) : null;
        PlaymatBounds = PlaymatSurface?.Bounds;
        PublicCounterArea = study ? TableSurfaceProjection.Surface(PlayerPlaymat.Project(seat, PlayerPlaymat.CanonicalCounterBand, duel)).InteriorBounds() : null;
        Hero = TableLayout.Hero(seat);
        Hand = TableLayout.SeatHand(seat);
        bool own = seat == 3, side = seat is 0 or 2;
        if (!study) {
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
        } else {
            // Personal cards use their seat's physical frame. The local life
            // counter stays centered; its zone row sits beside the near mat.
            if (own) {
                LandReadoutArea = new(8, 840, 300, 60);
            }
            if (side) {
                Hero -= new Vector2(0, 40);
                Hand = new(seat == 0 ? 84 : 1516, Hand.Y - 80);
            }
            ZoneStation = new(player, seat, duel, Hero);
            Commanders = ZoneStation.Commanders;
            Piles = ZoneStation.Piles;
            CommandLink = ZoneStation.CommandLabel.Center.ToVector2();
        }
        TurnControls = TurnControlLayout.For(Hero, study && own ? PlaymatBounds!.Value.Right + 12 : null);
    }

    internal static SeatGeometry[] Build(GameSnapshot state, bool study = false)
    {
        bool duel = study && state.Players.Length == 2;
        return state.Players.Select(player => new SeatGeometry(player,
            player.Id == state.ViewerId ? 3 : duel ? 1 : TableLayout.SeatIndex(state, player.Id), study, duel)).ToArray();
    }
}
