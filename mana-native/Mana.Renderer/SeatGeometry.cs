using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Seat objects shared by the scene and battlefield space reservations.</summary>
public sealed class SeatGeometry
{
    public int PlayerId { get; }
    public int Index { get; }
    public TableSeatFrame Frame { get; }
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

    private SeatGeometry(Player player, TableSeatFrame frame, bool study)
    {
        Frame = frame;
        int seat = frame.Index;
        PlayerId = player.Id;
        Index = seat;
        PlaymatPlacement = study ? PlayerPlaymat.Bounds(frame) : null;
        PlaymatSurface = PlaymatPlacement is { } placement ? TableSurfaceProjection.Surface(placement) : null;
        PlaymatBounds = PlaymatSurface?.Bounds;
        PublicCounterArea = study ? TableSurfaceProjection.Surface(frame.Project(PlayerPlaymat.CanonicalCounterBand)).InteriorBounds() : null;
        Hero = frame.Hero;
        Hand = frame.Hand;
        bool own = frame.IsLocal, side = frame.IsSide;
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
                LandReadoutArea = frame.LocalApron ? new(8, 840, 300, 60)
                    : new((int)Hero.X - 280, (int)Hero.Y - 25, 176, 52);
            }
            ZoneStation = new(player, frame, Hero);
            Commanders = ZoneStation.Commanders;
            Piles = ZoneStation.Piles;
            CommandLink = ZoneStation.CommandLabel.Center.ToVector2();
        }
        TurnControls = TurnControlLayout.For(Hero, study && own ? PlaymatBounds!.Value.Right + 12 : null);
    }

    internal static SeatGeometry[] Build(GameSnapshot state, bool study = false)
    {
        bool duel = study && state.Players.Length == 2;
        return state.Players.Select(player => {
            int index = study || state.Viewer == null || state.Players.Length > 4 ? TableSeatFrame.DisplayIndex(state, player.Id)
                : player.Id == state.ViewerId ? 3 : TableLayout.SeatIndex(state, player.Id);
            var frame = TableSeatFrame.For(index, state.Players.Length, study, duel);
            bool local = player.Id == state.ViewerId;
            return new SeatGeometry(player, frame with { IsLocal = local, LocalApron = local && frame.LocalApron }, study);
        }).ToArray();
    }
}
