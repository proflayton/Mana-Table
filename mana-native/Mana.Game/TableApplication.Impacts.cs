using System.Globalization;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record ImpactPaint(string Kind, long Amount, string? CardId, int? PlayerId, Rectangle Bounds)
    {
        public string DisplayId { get; init; } = "";
        public string[] AffectedCardIds { get; init; } = [];
        public int GroupMembers { get; init; } = 1;
        public string Caption { get; init; } = "";
    }
    private sealed record ImpactRecipient(string Id, IReadOnlyList<string> Members, Pose Pose, Rectangle Footprint);
    private sealed record AnchoredImpact(double Started, PublicImpact Impact, long Amount, ImpactRecipient Recipient);
    private readonly List<ImpactPaint> impactPaint = [];
    private void DrawActualImpacts()
    {
        impactPaint.Clear();
        if (match == null || OverlayOpen && !BattlefieldFocused) return;
        var occupied = CounterReceiptObstacles([]);
        if (hoverPreview is { } reader) occupied.Add(reader);
        foreach (var group in AnchoredActualImpacts()
                     .GroupBy(c => (c.Started, c.Impact.Kind, c.Recipient.Id, c.Impact.PlayerId,
                         c.Impact.DamageType, c.Impact.Combat, c.Impact.Infect, c.Amount))
                     .OrderByDescending(g => g.Key.Started)) {
            var impact = group.First().Impact;
            // Each member's same-observation hits were summed before grouping.
            // Equal per-member amounts share a receipt, never one summed hit on
            // the representative of a stack of identical creatures.
            long amount = group.Key.Amount;
            if (amount == 0) continue;
            double age = now - group.Key.Started;
            float fade = preferences.ReducedMotion ? 1 : (float)Math.Clamp((ImpactFeedback.Duration - age) / .5, 0, 1);
            bool damage = impact.Kind == "damage";
            var color = damage || amount < 0 ? Red : new Color(103, 228, 200);
            var recipient = group.First().Recipient;
            var pose = recipient.Pose;
            var affected = group.Select(c => c.Impact.CardId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            int members = Math.Max(1, recipient.Members.Count);
            bool subset = affected.Length > 0 && affected.Length < members;
            string caption = (damage ? "damage" : "life") + (affected.Length > 1 ? " each" : "")
                + (subset ? " ·" + affected.Length + "/" + members : "");
            float captionSize = affected.Length > 1 || subset ? .65f : .42f;
            if (impact.CardId != null) {
                canvas.CardFrame(pose with { Size = pose.Size + new Vector2(6) }, color * fade * .8f, 3);
            } else if (damage) canvas.Ring(pose.Center, 49, color * fade, 3);

            string number = (damage ? "" : amount > 0 ? "+" : "−")
                + (damage ? amount : Math.Abs(amount)).ToString(CultureInfo.InvariantCulture);
            int width = Math.Max(66, Math.Max(canvas.TextWidth(number, 1.1f, true) + 32,
                canvas.TextWidth(caption, captionSize, true) + 14));
            int captionHeight = canvas.TextSize(caption, captionSize, width - 8, 1, true).Y;
            int height = Math.Max(48, 31 + captionHeight);
            var anchor = pose.Point(damage ? 1 : 0, .30f);
            var bounds = EffectTransitionPresentation.ReceiptBounds(recipient.Footprint, anchor, new(width, height),
                new(10, 60, 1580, 720), occupied);
            if (bounds.IsEmpty) continue;
            occupied.Add(bounds);
            impactPaint.Add(new(impact.Kind, amount, impact.CardId, impact.PlayerId, bounds) {
                DisplayId = recipient.Id, AffectedCardIds = affected, GroupMembers = members, Caption = caption
            });
            var edge = new Pose(bounds.Center.ToVector2(), bounds.Size.ToVector2()).EdgeToward(anchor);
            canvas.Line(anchor, edge, color * fade * .7f, 2);
            canvas.Circle(anchor, 3, color * fade);
            canvas.Rounded(bounds, new Color(23, 22, 27) * fade, 6);
            // The red slash is the same visual family as marked damage; no minus
            // sign implies that a creature's effective toughness was reduced.
            if (damage) {
                var slash = new Vector2(bounds.X + 12, bounds.Y + 15);
                canvas.Line(slash + new Vector2(-3, 6), slash + new Vector2(4, -6), color * fade, 3);
            }
            canvas.Text(number, bounds.X + (damage ? 24 : 9), bounds.Y + 2, color * fade, 1.1f, bounds.Width - (damage ? 26 : 12), 1, true);
            canvas.CenterText(caption, new(bounds.X + 4, bounds.Bottom - captionHeight - 2, bounds.Width - 8, captionHeight),
                color * fade, captionSize, true);
        }
        // Draw counts are public facts; anonymous backs and a compact quantity
        // remain at that seat's hand instead of narrating them over the board.
        foreach (var cue in matchPresentation.SeatCues.Where(c => c.Kind == SeatCueKind.Draw && c.Amount > 1 && now - c.Started < 1.5)) {
            var hand = SeatHand(cue.PlayerId);
            var count = "+" + cue.Amount.ToString(CultureInfo.InvariantCulture);
            canvas.Text(count, (int)hand.X + 28, (int)hand.Y + 18, Blue, .8f, 150, 1, true);
        }
    }

    private IEnumerable<AnchoredImpact> AnchoredActualImpacts()
    {
        foreach (var events in matchPresentation.Impacts.Where(c => now - c.Started < ImpactFeedback.Duration
                && ImpactFeedback.RecipientVisible(match!, c.Impact, c.ActivityId))
            .GroupBy(c => (c.Started, c.Impact.Kind, c.Impact.CardId, c.Impact.PlayerId,
                c.Impact.DamageType, c.Impact.Combat, c.Impact.Infect))) {
            var first = events.First();
            ImpactRecipient? recipient = null;
            if (first.Impact.CardId is { } id) recipient = DisplayedImpactRecipient(id);
            else if (first.Impact.PlayerId is { } player) {
                var pose = new Pose(SeatHero(player), new(92, 92));
                recipient = new("player:" + player, [], pose, pose.Bounds);
            }
            if (recipient != null) yield return new(first.Started, first.Impact, events.Sum(c => c.Impact.Amount), recipient);
        }
    }

    private ImpactRecipient? DisplayedImpactRecipient(string id)
    {
        if (BattlefieldFocused && battlefieldFocusPlan?.Tile(id) is { } tile) {
            if (!battlefieldFocusPlan.Viewport.Contains(tile.Art)) return null;
            return new(tile.Group.Id, tile.Group.MemberIds, tile.ArtPose, tile.Bounds);
        }
        if (matchPresentation.Departures.LastOrDefault(d => d.Card.VisualId == id && now - d.Started <= 1.5) is { } departure) {
            var pose = DeparturePose(departure);
            if (!BattlefieldFocused || !pose.Bounds.Intersects(BattlefieldFocusBounds)) return new(id, [id], pose, pose.Bounds);
            return null;
        }
        // A collapsed stack still identifies every occurrence. Attach the result
        // to its visible front rather than drawing at a covered copy's centre.
        if (!BattlefieldFocused && tableScene?.Cohort(id) is { } cohort
            && matchPresentation.Cards.TryGetPose(cohort.Front.Id, out var front))
            return new(cohort.Front.Id, cohort.MemberIds, front, front.Bounds);
        var entry = matchPresentation.Cards.Cards.LastOrDefault(card => (card.Id == id || CardViews.Identity(card.Card) == id)
            && card.Zone is "Battlefield" or "Command" or "Stack" or "Graveyard" or "Exile");
        if (entry == null || BattlefieldFocused && entry.Display.Bounds.Intersects(BattlefieldFocusBounds)) return null;
        return new(entry.Id, [id], entry.Display, entry.Display.Bounds);
    }
}
