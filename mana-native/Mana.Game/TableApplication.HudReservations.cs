using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Captured explicitly once per composition, before HUD placement. This
    // owner accepts scene furniture and retained card motion; reader/payment
    // paint from a previous composition is never an input to required HUD.
    private sealed record StudyHudReservations(IReadOnlyList<Rectangle> Attention,
        IReadOnlyList<Rectangle> Fixed, IReadOnlyList<Rectangle> All)
    {
        internal static readonly StudyHudReservations Empty = new([], [], []);
    }

    private void PrepareStudyHudReservations()
    {
        studyHudReservations = StudyHudReservations.Empty;
        if (!TableStudyEnabled || match is not { } state || tableScene == null) return;
        var occupied = new List<Rectangle>();
        void AddCard(Pose pose) {
            var body = new CardPaintGeometry(pose);
            occupied.Add(body.Backing.Bounds); occupied.Add(body.Shadow.Bounds);
        }
        foreach (var card in tableScene.BattlefieldDrawOrder) AddCard(boardView.Apply(card.Pose));
        int backgroundCount = occupied.Count;
        foreach (var card in tableScene.Hand.Cards) AddCard(card.Pose);
        foreach (var card in tableScene.PlayAccess.Groups.SelectMany(group => group.Cards)) AddCard(card.Scene.Pose);
        foreach (var seat in tableScene.Seats) {
            var player = state.Players.Single(player => player.Id == seat.PlayerId);
            bool own = seat.PlayerId == state.ViewerId;
            var hero = seat.Geometry.Hero;
            int radius = own ? 47 : 41;
            occupied.Add(new((int)hero.X - radius, (int)hero.Y - radius, radius * 2, radius * 2 + 6));
            var mana = PlanManaHalo(player, seat.Index);
            occupied.AddRange(mana.Parts.Select(part => part.Bounds));
            if (!own) {
                if (!seatReadoutPlans.ContainsKey(seat.PlayerId))
                    occupied.Add(SeatNameplateArea(hero, false, seat.Index, mana));
                foreach (var hand in OpponentHandPoses(player, seat.Index)) {
                    occupied.Add(hand.Bounds);
                    occupied.Add((hand with { Center = hand.Center + new Vector2(3, 6) }).Bounds);
                }
                bool far = state.Players.Length > 4 ? seat.Geometry.Frame.IsFar : seat.Index == 1;
                if (!far && StateClarity.PlayerStates(player).Length == 0)
                    occupied.Add(new((int)seat.Geometry.Hand.X - 65, (int)seat.Geometry.Hand.Y + 35, 130, 22));
                if (OpponentPhaseCenter(player, hero, seat.Index) is { } phase) {
                    occupied.Add(new((int)phase.X - 15, (int)phase.Y - 15, 30, 30));
                }
                if (seatReadoutPlans.TryGetValue(player.Id, out var readout)) occupied.AddRange(readout.Pieces);
            } else {
                occupied.AddRange(seat.Geometry.TurnControls.PhaseBounds);
                occupied.Add(seat.Geometry.TurnControls.HoldBounds);
                occupied.Add(seat.Geometry.TurnControls.AlertBounds);
                if (StateClarity.PlayerStates(player).Length > 0 && seat.Geometry.PublicCounterArea is { } counters) occupied.Add(counters);
                if (seat.Geometry.LandReadoutArea is { } landArea) occupied.Add(landArea);
            }
            foreach (var commander in seat.Geometry.Commanders) {
                var pose = commander.Pose with { Size = commander.Pose.Size + new Vector2(13) };
                AddCard(commander.TableAnchored ? boardView.Apply(pose) : pose);
            }
            occupied.Add(DisplayCommandLabel(seat.Geometry));
            if (AvailableZoneCount(player, "Command") > 0)
                occupied.Add(DisplayCommandActions(seat.Geometry));
            foreach (var pile in seat.Geometry.Piles) {
                var paint = DisplayPileGeometry(pile, player.Zone(pile.Zone).Count);
                occupied.Add(paint.Shadow.Bounds); occupied.Add(paint.Top.Bounds); occupied.AddRange(paint.Layers.Select(layer => layer.Bounds));
                occupied.Add(paint.Label);
                if (!paint.Count.IsEmpty) occupied.Add(paint.Count);
            }
        }
        foreach (var card in matchPresentation.Cards.Cards.Where(card => card.Focused
            || card.Zone != "Stack" && (card.Layer is CardPresentationLayer.Returning or CardPresentationLayer.Playing))) AddCard(card.Display);
        if (matchPresentation.Cards.Pinned?.ReaderTarget is { } reader) AddCard(reader);
        // The attention area owns these unpinned stack destinations. Its
        // moving spell cannot also repel that destination every frame.
        // Other HUD consumers still avoid the complete physical journey.
        var attention = Array.AsReadOnly(occupied.Where(bounds => !bounds.IsEmpty).ToArray());
        var fixedHud = Array.AsReadOnly(occupied.Skip(backgroundCount).Where(bounds => !bounds.IsEmpty).ToArray());
        foreach (var card in matchPresentation.Cards.Cards.Where(card => !card.Focused && card.Zone == "Stack"
            && (card.Layer is CardPresentationLayer.Returning or CardPresentationLayer.Playing))) AddCard(card.Display);
        studyHudReservations = new(attention, fixedHud, Array.AsReadOnly(occupied.Where(bounds => !bounds.IsEmpty).ToArray()));
    }
}
