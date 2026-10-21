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

    private void DrawEffectTransitions()
    {
        effectTransitionFrame = EffectTransitionFrame.Empty;
        if (OverlayOpen && !BattlefieldFocused || readingId != null || tableScene == null) return;
        var visible = cardPresentation.Cards.Where(card => card.Zone is "Battlefield" or "Command" or "Stack" or "Graveyard" or "Exile")
            .GroupBy(card => CardViews.Identity(card.Card), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.Last().Display, StringComparer.Ordinal);
        var groups = new List<EffectPermanentGroup>();
        if (BattlefieldFocused && battlefieldFocusPlan is { } focus) {
            foreach (string id in visible.Where(entry => entry.Value.Bounds.Intersects(BattlefieldFocusBounds)).Select(entry => entry.Key).ToArray()) visible.Remove(id);
            foreach (var tile in focus.Tiles.Where(tile => focus.Viewport.Contains(tile.Art))) {
                var pose = new Pose(tile.Art.Center.ToVector2(), tile.Art.Size.ToVector2());
                var ids = tile.Group.Cards.Select(CardViews.Identity).Where(id => id.Length > 0).ToArray();
                foreach (string id in ids) visible[id] = pose;
                var details = MeasureBattlefieldFocusDetails(tile.Group.Front, tile.Details.Width);
                var counter = details.Lines.FirstOrDefault(line => line.Chip);
                var landing = counter == null ? pose.Point(.8f, .82f)
                    : new Vector2(tile.Details.Right - 16, tile.Details.Y + counter.Y + counter.Height / 2);
                groups.Add(new(tile.Group.Id, Array.AsReadOnly(ids), pose, landing));
            }
            if (publicChanges.Latest is { } latest && latest.Cause?.CardId is { } sourceId && !visible.ContainsKey(sourceId)
                && ChangeSourcePreview(latest) is { } sourcePreview) visible[sourceId] = sourcePreview.Pose;
        } else foreach (var seat in tableScene.Seats) foreach (var cluster in seat.Battlefield) {
            var copies = new List<SceneCard>();
            foreach (var node in cluster.Cards) {
                copies.Add(node);
                if (!node.GroupFront) continue;
                if (visible.TryGetValue(node.Id, out var pose)) groups.Add(new(node.Id,
                    Array.AsReadOnly(copies.Select(copy => copy.Id).Where(visible.ContainsKey).ToArray()), pose, CounterLanding(node, pose)));
                copies.Clear();
            }
        }
        // Protect the actual decision controls. A source preview above them
        // remains a visible, reported origin when its physical card is offscreen.
        int protectedTop = Math.Max(550, (QuietPriorityControlVisible ? QuietPriorityControlTop : decisionPanelBounds.Top) - 8);
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
        foreach (var target in effectTransitionFrame.Targets.Where(target => target.Landed)
            .GroupBy(target => (target.Target.FrontId, target.Delta.Property))
            .Select(group => group.MaxBy(target => target.LastActivityId)!)) {
            var accent = target.Delta.Total < 0 ? Red : new Color(103, 228, 200);
            if (!preferences.ReducedMotion)
                canvas.Ring(target.Target.CounterLanding, 12 + (1 - target.Strength) * 17,
                    accent * target.Strength, 3);
            PaintEffectCounterTile(target.Label, target.Target.CounterLanding, accent, target.Strength);
        }
        foreach (var flight in effectTransitionFrame.Flights) {
            var accent = flight.Delta.Total < 0 ? Red : new Color(103, 228, 200);
            var target = effectTransitionFrame.Targets.LastOrDefault(target => target.EffectId == flight.EffectId
                && target.Target.FrontId == flight.FrontId && target.Delta.Property == flight.Delta.Property);
            PaintEffectCounterTile(target?.Label ?? flight.Delta.Label, flight.Position, accent, 1);
            PaintEffectSpark(flight.Position - new Vector2(0, 12), 4, Gold);
        }
    }

    private Vector2 CounterLanding(SceneCard node, Pose displayed)
    {
        if (node.Dense && !node.DenseLabels.IsEmpty && CardPresentation.Counters(node.Card).Length > 0) {
            int row = 1 + (CardPresentation.HasCombatStats(node.Card) || node.GroupCount > 1 ? 1 : 0);
            return boardView.Apply(new Vector2(node.DenseLabels.Right - 12, node.DenseLabels.Top + row * node.DenseRowHeight + node.DenseRowHeight / 2));
        }
        return displayed.Point(.80f, .82f);
    }

    private void PaintEffectCounterTile(string label, Vector2 point, Color accent, float strength)
    {
        float size = BattlefieldFocused ? .60f : .4f;
        int height = BattlefieldFocused ? 26 : 20;
        int width = canvas.TextWidth(label, size, true) + 8;
        var bounds = new Rectangle((int)MathF.Round(point.X) - width / 2, (int)MathF.Round(point.Y) - height / 2, width, height);
        canvas.Panel(bounds, new Color(12, 29, 30) * Math.Max(.5f, strength), accent * Math.Max(.5f, strength), 3);
        canvas.CenterText(label, bounds, accent, size, true);
    }

    private void PaintEffectSpark(Vector2 point, float radius, Color accent)
    {
        canvas.Polygon([point + new Vector2(0, -radius), point + new Vector2(radius * .30f, -radius * .30f),
            point + new Vector2(radius, 0), point + new Vector2(radius * .30f, radius * .30f),
            point + new Vector2(0, radius), point + new Vector2(-radius * .30f, radius * .30f),
            point + new Vector2(-radius, 0), point + new Vector2(-radius * .30f, -radius * .30f)], accent);
    }
}
