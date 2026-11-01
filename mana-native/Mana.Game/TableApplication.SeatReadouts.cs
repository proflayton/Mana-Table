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
            if (seat.Index != 1 && StateClarity.PlayerStates(player).Length > 0)
                caption += (caption.Length > 0 ? " · " : "") + player.Zone("Hand").Count + " in hand";
            SeatReadoutPlan Build(int width = 224, bool? horizontal = null, bool countedOnly = false) => SeatReadoutPlan.Build(player, seat.Index, PublicSeatOrdinal(player.Id), caption, incoming > 0,
                (text, size, bold) => canvas.TextWidth(text, size, bold),
                (text, size, measureWidth, bold) => canvas.TextSize(text, size, measureWidth, 80, bold), width, horizontal, countedOnly);
            return (Player: player.Id, Reserved: Build(countedOnly: true), Arrangements: new[] { Build(), Build(264), Build(horizontal: false) });
        }).ToArray();
        foreach (var candidate in candidates) {
            var protectedAreas = occupied.Concat(candidates.Where(other => other.Player != candidate.Player).SelectMany(other => other.Reserved.Pieces)).ToArray();
            switch (seatReadoutPlacer(candidate.Arrangements, candidate.Reserved, BoardViewport, protectedAreas)) {
                case SeatReadoutPlacement.Placed placed:
                    seatReadoutPlans[candidate.Player] = placed.Plan;
                    occupied.AddRange(placed.Plan.Pieces);
                    break;
                case SeatReadoutPlacement.NoFit failure:
                    seatReadoutWorkspaces.Add(failure.PlayerId);
                    break;
            }
        }
    }

    private List<Rectangle> FixedSeatReadoutObstacles()
    {
        var occupied = new List<Rectangle> { StudyFocusNavigationBounds };
        foreach (var seat in tableScene!.Seats) {
            var player = match!.Players.Single(player => player.Id == seat.PlayerId);
            var geometry = seat.Geometry;
            if (geometry.PlaymatBounds is { } mat) occupied.Add(mat);
            occupied.Add(new((int)geometry.Hero.X - 47, (int)geometry.Hero.Y - 47, 94, 100));
            var mana = PlanManaHalo(player, seat.Index);
            occupied.AddRange(mana.Parts.Select(part => part.Bounds));
            occupied.Add(SeatNameplateArea(geometry.Hero, seat.Index == 3, seat.Index, mana));
            if (seat.Index != 3) {
                occupied.Add(new((int)geometry.Hand.X - 83, (int)geometry.Hand.Y - 30, 166, 75));
                foreach (var hand in OpponentHandPoses(player, seat.Index)) {
                    occupied.Add(hand.Bounds); occupied.Add((hand with { Center = hand.Center + new Vector2(3, 6) }).Bounds);
                }
            } else occupied.AddRange(geometry.TurnControls.Reservations);
            foreach (var card in geometry.Commanders) occupied.Add((card.Pose with { Size = card.Pose.Size + new Vector2(13) }).Bounds);
            var command = geometry.CommandLink;
            // Reserve the complete label/action rail even before a cast becomes
            // available, so priority and camera changes cannot move the HUD.
            occupied.Add(new((int)command.X - 56, (int)command.Y - 3, 112, 52));
            foreach (var pile in geometry.Piles)
                occupied.Add(PileGeometry.Create(pile.Pose, player.Zone(pile.Zone).Count, pile.Compact).Reservation);
        }
        return occupied;
    }

    private void DrawSeatReadout(SeatReadoutPlan plan)
    {
        foreach (var item in plan.Text) {
            var color = item.Role == SeatReadoutTextRole.Incoming ? Red : item.Role == SeatReadoutTextRole.Status ? Gold : Ink;
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
