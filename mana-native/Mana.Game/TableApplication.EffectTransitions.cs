using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private EffectTransitionFrame effectTransitionFrame = EffectTransitionFrame.Empty;
    internal sealed record CounterReceiptPaint(string EffectId, string FrontId, IReadOnlyList<string> AffectedIds,
        string Property, string Label, Rectangle Bounds, Vector2 Anchor);
    private readonly List<CounterReceiptPaint> counterReceiptPaint = [];

    private void DrawEffectTransitions()
    {
        effectTransitionFrame = EffectTransitionFrame.Empty;
        counterReceiptPaint.Clear();
        if (OverlayOpen && !BattlefieldFocused || readingId != null || tableScene == null) return;
        var visible = cardPresentation.Cards.Where(card => card.Zone is "Battlefield" or "Command" or "Stack" or "Graveyard" or "Exile")
            .GroupBy(card => CardViews.Identity(card.Card), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0).ToDictionary(group => group.Key,
                group => group.FirstOrDefault(card => card.Zone == "Battlefield")?.Display ?? group.Last().Display, StringComparer.Ordinal);
        var groups = new List<EffectPermanentGroup>();
        if (BattlefieldFocused && battlefieldFocusPlan is { } focus) {
            foreach (string id in visible.Where(entry => entry.Value.Bounds.Intersects(BattlefieldFocusBounds)).Select(entry => entry.Key).ToArray()) visible.Remove(id);
            foreach (var tile in focus.VisibleTiles) {
                var pose = tile.ArtPose;
                var ids = tile.Group.MemberIds;
                foreach (string id in ids) visible[id] = pose;
                var details = MeasureBattlefieldFocusDetails(tile.Group.Front, tile.Details.Width);
                var counter = details.Lines.FirstOrDefault(line => line.Chip);
                var landing = counter == null ? pose.Point(.8f, .82f)
                    : new Vector2(tile.Details.Right - 16, tile.Details.Y + counter.Y + counter.Height / 2);
                groups.Add(new(tile.Group.Id, ids, pose, landing) { ReceiptExclusion = tile.Bounds });
            }
        } else foreach (var cohort in tableScene.Seats.SelectMany(seat => seat.Cohorts))
            if (visible.TryGetValue(cohort.Front.Id, out var pose)) groups.Add(new(cohort.Front.Id,
                Array.AsReadOnly(cohort.MemberIds.Where(visible.ContainsKey).ToArray()), pose, CounterLanding(cohort.Front, pose)));
        if (publicChanges.Latest is { } latest && latest.Cause?.CardId is { } sourceId
            && ChangeSourcePreview(latest) is { } sourcePreview) visible[sourceId] = sourcePreview.Pose;
        // Protect the actual decision controls. A source preview above them
        // remains a visible, reported origin when its physical card is offscreen.
        int protectedTop = Math.Max(550, decisionPanelBounds.Top - 8);
        effectTransitionFrame = EffectTransitionPresentation.Compose(publicChanges.Groups, groups, visible,
            now, preferences.ReducedMotion, new(1240, protectedTop, 350, 900 - protectedTop));
        foreach (var source in effectTransitionFrame.Sources) {
            float radius = Math.Max(21, Math.Max(source.Pose.Size.X, source.Pose.Size.Y) / 2 + source.Expansion);
            canvas.Ring(source.Pose.Center, radius, Gold * source.Strength, 3);
            canvas.CardFrame(source.Pose with { Size = source.Pose.Size + new Vector2(8) }, Gold * source.Strength, 3);
            var badge = source.Pose.Point(1, 0) + new Vector2(1, -2);
            canvas.Circle(badge, 8, new(26, 29, 24));
            PaintEffectSpark(badge, 6, Gold);
        }
        foreach (var target in effectTransitionFrame.Targets) {
            var accent = target.Delta.Total < 0 ? Red : new Color(103, 228, 200);
            canvas.CardFrame(target.Target.Pose with { Size = target.Target.Pose.Size + new Vector2(5) }, accent * target.Strength, 2);
        }
        var occupied = CounterReceiptObstacles(groups);
        string? hoveredCard = HitAt(pointer)?.Card is { } hovered ? CardViews.Identity(hovered) : null;
        foreach (var target in effectTransitionFrame.Targets.Where(target => target.Landed)
            .GroupBy(target => (target.Target.FrontId, target.Delta.Property))
            .Select(group => group.MaxBy(target => target.LastActivityId)!)
            .OrderByDescending(target => target.AffectedIds.Contains(hoveredCard ?? ""))
            .ThenByDescending(target => match?.Viewer?.Zone("Battlefield").Cards.Any(card => target.AffectedIds.Contains(CardViews.Identity(card))) == true)
            .ThenByDescending(target => target.LastActivityId)) {
            var accent = target.Delta.Total < 0 ? Red : new Color(103, 228, 200);
            if (!preferences.ReducedMotion)
                canvas.Ring(target.Target.CounterLanding, 7 + (1 - target.Strength) * 7,
                    accent * target.Strength, 2);
            DrawCounterImpactReceipt(target, accent, occupied);
        }
        foreach (var flight in effectTransitionFrame.Flights) {
            var accent = flight.Delta.Total < 0 ? Red : new Color(103, 228, 200);
            PaintEffectSpark(flight.Position, 4, accent);
        }
    }

    private List<Rectangle> CounterReceiptObstacles(IReadOnlyList<EffectPermanentGroup> groups)
    {
        var occupied = cardPresentation.Cards.Select(card => card.Display.Bounds)
            .Concat(groups.Where(group => !group.ReceiptExclusion.IsEmpty).Select(group => group.ReceiptExclusion))
            .Concat(counterReceiptPaint.Select(receipt => receipt.Bounds))
            .Concat(stackRows.Select(row => row.Bounds)).ToList();
        if (TableStudyEnabled && tableScene != null) {
            foreach (var family in tableScene.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards)
                .Select(card => card.StudyFamily).OfType<StudyBattlefieldFamily>().DistinctBy(family => family.Id)
                .Where(family => family.CohortCount > 1 && StudyFamilyNameVisible(family))) {
                // These captions sit outside the individual card poses. Reserve
                // the same raw area DrawStudyFamily paints, including when a
                // crowded local field remains compact in the focused table.
                occupied.Add(family.NameBounds);
            }
        }
        if (BattlefieldFocused && battlefieldFocusPlan is { } focus) {
            occupied.AddRange(focus.VisibleTiles.Select(tile => tile.Bounds));
            occupied.AddRange(focus.Headings.Select(heading => Rectangle.Intersect(heading.Bounds, focus.Viewport)).Where(bounds => !bounds.IsEmpty));
            occupied.Add(new(160, 120, 1080, 100));
            occupied.Add(new(160, 730, 1080, 70));
        }
        if (!decisionPanelBounds.IsEmpty) occupied.Add(decisionPanelBounds);
        if (!targetingEffectBounds.IsEmpty) occupied.Add(targetingEffectBounds);
        if (pendingPreviewBounds is { } preview) occupied.Add(preview);
        if (publicChanges.Latest is { } latest && ChangeSourcePreview(latest) != null)
            occupied.Add(ChangeBridgeBounds);
        foreach (var hero in heroPositions) {
            int radius = hero.Key == match?.ViewerId ? 58 : 48;
            occupied.Add(new((int)hero.Value.X - radius, (int)hero.Value.Y - radius, radius * 2, radius * 2));
        }
        // The anchored hand and header are drawn later in the frame.
        occupied.Add(new(0, 0, 1600, 60));
        occupied.Add(new(250, 780, 1000, 120));
        return occupied;
    }

    private void DrawCounterImpactReceipt(EffectTargetImpact target, Color accent, List<Rectangle> occupied)
    {
        // The ordinary P/T-counter burst uses the same counter glyph as the
        // settled badge. Repeating '+1 +1/+1' on every affected group makes the
        // increment harder to see; its exact type remains in card inspection.
        bool familiar = target.Delta.Property == "+1/+1";
        string label = target.Label + (familiar ? "" : " " + target.Delta.Property);
        int glyphWidth = familiar ? 18 : 0;
        const float size = .66f;
        int textWidth = Math.Min(220, canvas.TextWidth(label, size, true));
        var measured = canvas.TextSize(label, size, textWidth, 80, true);
        var footprint = target.Target.ReceiptExclusion.IsEmpty ? target.Target.Pose.Bounds : target.Target.ReceiptExclusion;
        var bounds = EffectTransitionPresentation.ReceiptBounds(footprint, target.Target.CounterLanding,
            new(measured.X + 14 + glyphWidth, measured.Y + 8), new(10, 60, 1580, 720), occupied);
        if (bounds.IsEmpty) return; // Current totals and exact inspectable state retain their own space.
        occupied.Add(bounds);
        float strength = Math.Max(.75f, target.Strength);
        var end = new Pose(bounds.Center.ToVector2(), bounds.Size.ToVector2()).EdgeToward(target.Target.CounterLanding);
        canvas.Line(target.Target.CounterLanding, end, accent * .65f, 1.5f);
        canvas.Circle(target.Target.CounterLanding, 2.5f, accent);
        canvas.Rounded(bounds, new Color(14, 30, 32) * strength, 4);
        if (familiar) PaintCounterGlyph(new(bounds.X + 6, bounds.Center.Y - 5, 11, 11), accent * strength);
        canvas.Text(label, bounds.X + 7 + glyphWidth, bounds.Y + 4, accent * strength, size, bounds.Width - 14 - glyphWidth, 80, true);
        counterReceiptPaint.Add(new(target.EffectId, target.Target.FrontId, target.AffectedIds,
            target.Delta.Property, label, bounds, target.Target.CounterLanding));
    }

    private Vector2 CounterLanding(SceneCard node, Pose displayed)
    {
        if (counterAnchors.TryGetValue(node.Id, out var anchor)) return anchor;
        return displayed.Point(.80f, .82f);
    }

    private void PaintEffectSpark(Vector2 point, float radius, Color accent)
    {
        canvas.Polygon([point + new Vector2(0, -radius), point + new Vector2(radius * .30f, -radius * .30f),
            point + new Vector2(radius, 0), point + new Vector2(radius * .30f, radius * .30f),
            point + new Vector2(0, radius), point + new Vector2(-radius * .30f, radius * .30f),
            point + new Vector2(-radius, 0), point + new Vector2(-radius * .30f, -radius * .30f)], accent);
    }
}
