using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly Dictionary<int, SeatReadoutPlan> seatReadoutPlans = [];
    private readonly HashSet<int> seatReadoutWorkspaces = [];
    private SeatReadoutPlacer seatReadoutPlacer = SeatReadoutPlan.Place;
    private int PublicSeatOrdinal(int playerId) => match!.Players.Select((player, index) => (player.Id, Number: index + 1))
        .First(item => item.Id == playerId).Number;

    private void PrepareSeatReadouts()
    {
        seatReadoutPlans.Clear();
        seatReadoutWorkspaces.Clear();
        if (!TableStudyEnabled || match == null || tableScene == null) return;
        var occupied = FixedSeatReadoutObstacles();
        var candidates = tableScene.Seats.Where(seat => seat.PlayerId != match.ViewerId).Select(seat => {
            var player = match.Players.Single(player => player.Id == seat.PlayerId);
            int incoming = match.Combat?.Attackers.Count(attack => attack.HasDefender && attack.DefendingPlayerId == player.Id) ?? 0;
            string caption = player.Eliminated ? TimedOut(player.Id) ? "TIMED OUT" : "ELIMINATED" : incoming > 0 ? incoming + " INCOMING" : "";
            bool extended = match.Players.Length > 4;
            bool far = extended ? seat.Geometry.Frame.IsFar : seat.Index == 1;
            if (!far && StateClarity.PlayerStates(player).Length > 0)
                caption += (caption.Length > 0 ? " · " : "") + player.Zone("Hand").Count + " in hand";
            int? alignment = !extended ? null : 1;
            SeatReadoutPlan Build(int width = 224, bool? horizontal = null, bool countedOnly = false) => SeatReadoutPlan.Build(player, seat.Index, PublicSeatOrdinal(player.Id), caption, incoming > 0,
                (text, size, bold) => canvas.TextWidth(text, size, bold),
                (text, size, measureWidth, bold) => canvas.TextSize(text, size, measureWidth, 80, bold), width,
                horizontal ?? (extended ? (bool?)far : null), countedOnly,
                SeatReadoutPlan.RelationshipLabel(match, player.Id)).AtPortrait(seat.Geometry.Hero, alignment);
            var identitySlot = SeatNameplateArea(seat.Geometry.Hero, false, seat.Index, PlanManaHalo(player, seat.Index));
            return (Player: player.Id, IdentitySlot: identitySlot, Reserved: Build(countedOnly: true), Arrangements: new[] { Build(), Build(264), Build(horizontal: false) });
        }).ToArray();
        foreach (var candidate in candidates) {
            occupied.Remove(candidate.IdentitySlot);
            var protectedAreas = occupied.Concat(candidates.Where(other => other.Player != candidate.Player
                && !seatReadoutPlans.ContainsKey(other.Player) && !seatReadoutWorkspaces.Contains(other.Player)).SelectMany(other => other.Reserved.Pieces)).ToArray();
            var owner = tableScene.Seat(candidate.Player).Geometry.Hero;
            var readoutViewport = match.Players.Length > 4
                ? Rectangle.Intersect(new(0, 54, 1600, 846), new((int)owner.X - 180, (int)owner.Y + 52, 360, 150))
                : BoardViewport;
            switch (seatReadoutPlacer(candidate.Arrangements, candidate.Reserved, readoutViewport, protectedAreas)) {
                case SeatReadoutPlacement.Placed placed:
                    seatReadoutPlans[candidate.Player] = placed.Plan;
                    occupied.AddRange(placed.Plan.Pieces);
                    break;
                case SeatReadoutPlacement.NoFit failure:
                    seatReadoutWorkspaces.Add(failure.PlayerId);
                    occupied.Add(candidate.IdentitySlot);
                    break;
            }
        }
    }

    private List<Rectangle> FixedSeatReadoutObstacles()
    {
        var occupied = new List<Rectangle> { StudyFocusNavigationBounds };
        var overview = TableViewGeometry.Overview(tableScene!);
        bool extended = match!.Players.Length > 4;
        if (extended) occupied.AddRange(tableScene!.Hand.Cards.Select(card => card.Pose.Bounds));
        foreach (var seat in tableScene!.Seats) {
            var player = match!.Players.Single(player => player.Id == seat.PlayerId);
            var geometry = seat.Geometry;
            if (geometry.PlaymatBounds is { } mat) occupied.Add(overview.Apply(mat));
            occupied.Add(new((int)geometry.Hero.X - 47, (int)geometry.Hero.Y - 47, 94, 100));
            var mana = PlanManaHalo(player, seat.Index);
            occupied.AddRange(mana.Parts.Select(part => part.Bounds));
            occupied.Add(SeatNameplateArea(geometry.Hero, geometry.Frame.IsLocal, seat.Index, mana));
            if (!geometry.Frame.IsLocal) {
                occupied.Add(new((int)geometry.Hand.X - 83, (int)geometry.Hand.Y - 30, 166, 75));
                foreach (var hand in OpponentHandPoses(player, seat.Index)) {
                    occupied.Add(hand.Bounds); occupied.Add((hand with { Center = hand.Center + new Vector2(3, 6) }).Bounds);
                }
            } else if (extended) {
                // Owner identities are fixed seat furniture. The priority dock
                // chooses its actual free position after these readouts exist;
                // its preferred rectangle must not displace their identities.
                occupied.AddRange(geometry.TurnControls.PhaseBounds);
                occupied.Add(geometry.TurnControls.AutoBounds);
                occupied.Add(geometry.TurnControls.HoldBounds);
                occupied.Add(geometry.TurnControls.AlertBounds);
                if (geometry.LandReadoutArea is { } lands) occupied.Add(lands);
            } else occupied.AddRange(geometry.TurnControls.Reservations);
            foreach (var card in geometry.Commanders) occupied.Add(overview.Apply(card.Pose with { Size = card.Pose.Size + new Vector2(13) }).Bounds);
            // Reserve the complete label/action rail even before a cast becomes
            // available, so priority and camera changes cannot move the HUD.
            if (geometry.ZoneStation is { } station) {
                occupied.Add(overview.Apply(station.CommandLabel)); occupied.Add(overview.Apply(station.CommandActions));
            } else {
                var command = geometry.CommandLink;
                occupied.Add(new((int)command.X - 56, (int)command.Y - 3, 112, 52));
            }
            foreach (var pile in geometry.Piles)
                occupied.Add(pile.TableAnchored ? overview.Apply(PileGeometry.Create(pile, player.Zone(pile.Zone).Count).Reservation)
                    : PileGeometry.Create(pile, player.Zone(pile.Zone).Count).Reservation);
        }
        return occupied;
    }

    private void DrawSeatReadout(SeatReadoutPlan plan)
    {
        foreach (var item in plan.Text) {
            var color = item.Role == SeatReadoutTextRole.Incoming ? Red : item.Role == SeatReadoutTextRole.Status ? Gold : Ink;
            if (item.Role == SeatReadoutTextRole.LandTotal && item.Bounds.Contains(pointer)) color = Blue;
            canvas.Text(item.Text, item.Bounds.X, item.Bounds.Y, color, item.Size, item.Bounds.Width, 80, item.Bold);
            seatReadoutBounds.Add(item.Bounds);
        }
        var painted = new List<PublicStateChipPaint>();
        var targets = plan.States.Select(item => item.Bounds).ToArray();
        if (targets.Length > 0) hits.Add(new("player-counters:" + plan.PlayerId, Scope, plan.CounterBounds,
            new MatchIntent.OpenPlayerState(plan.PlayerId, ""), Shape: point => targets.Any(bounds => bounds.Contains(point))));
        foreach (var item in plan.States) {
            string id = item.More ? "player-state-more:" + plan.PlayerId : "player-state:" + plan.PlayerId + ":" + item.Id;
            PaintControlSurface(id, item.Bounds, new(14, 26, 31), item.More ? Muted : item.Kind == "progress" ? Gold : Blue);
            canvas.Text(item.Text, item.Bounds.X + 6, item.Bounds.Y + 2, Ink, item.More ? .53f : .6f, item.Bounds.Width - 12, 80, true);
            hits.Add(new(id, Scope, item.Bounds, new MatchIntent.OpenPlayerState(plan.PlayerId, item.Id)));
            if (!item.More) painted.Add(new(item.Id, item.Text, item.Bounds, item.MeasuredWidth, item.Bounds.Width - 12));
        }
        if (targets.Length > 0) publicCounterBadges[plan.PlayerId] = new(string.Join(" · ", plan.States.Select(item => item.Text)),
            .6f, painted.Select(item => item.MeasuredWidth).DefaultIfEmpty(0).Max(), 212, plan.HiddenStates) {
                Bounds = plan.CounterBounds, Chips = painted.ToArray()
            };
    }
}
