using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    internal sealed record AttachmentBadgePaint(string Text, Rectangle Bounds, int MeasuredWidth, bool Compact, bool IncludesCombatStats = false);
    internal sealed record AttachmentConnection(string SourceId, string TargetId, Vector2 Start, Vector2 End);
    private readonly Dictionary<string, AttachmentBadgePaint> attachmentBadges = [];
    private readonly List<AttachmentConnection> attachmentConnections = [];

    private void DrawAttachmentConnections()
    {
        if (OverlayOpen || ActivityOpen || updatesOpen || ExperienceOpen || dragging) return;
        // Draw() clears hovered before rebuilding the current scene. Resolve the
        // current hit after battlefield cards have registered their actual poses.
        string? focus = HitAt(pointer)?.Card?.CombatId;
        if (string.IsNullOrEmpty(focus)) return;
        foreach (var card in match!.Players.SelectMany(p => p.Zone("Battlefield").Cards)) {
            if (card.AttachedTo is not { } target || card.CombatId != focus && target.Id != focus) continue;
            if (!cardPositions.TryGetValue(card.CombatId, out var sourcePose) || !cardPositions.TryGetValue(target.Id, out var targetPose)) continue;
            var start = sourcePose.EdgeToward(targetPose.Center, 4);
            var end = targetPose.EdgeToward(sourcePose.Center, 4);
            canvas.Line(start, end, Gold * .85f, 2);
            canvas.Circle(start, 3, Gold); canvas.Circle(end, 3, Gold);
            var linked = card.CombatId == focus ? targetPose : sourcePose;
            canvas.CardFrame(linked with { Size = linked.Size + new Vector2(7) }, Gold * .85f, 2);
            attachmentConnections.Add(new(card.CombatId, target.Id, start, end));
        }
    }
    private void PaintAttachmentBadge(Card card, Pose pose, int owner, bool compact)
    {
        string label = CardPresentation.AttachmentBadge(card);
        if (label.Length == 0) return;
        if (pose.Size.X < 300 && tableScene?.BattlefieldCard(owner, CardViews.Identity(card)) is { Dense: true, DenseLabels.IsEmpty: true }) {
            // The extreme face-only fallback has no room for a detached stats
            // chip. Keep its relationship cue inside this exact face instead.
            int side = Math.Min(10, Math.Min(pose.Bounds.Width, pose.Bounds.Height) - 2);
            Rectangle cue = Rectangle.Empty;
            while (side > 0) {
                cue = new((int)pose.Center.X - side / 2, (int)pose.Center.Y - side / 2, side, side);
                if (pose.Contains(new(cue.Left, cue.Top)) && pose.Contains(new(cue.Right - 1, cue.Top))
                    && pose.Contains(new(cue.Left, cue.Bottom - 1)) && pose.Contains(new(cue.Right - 1, cue.Bottom - 1))) break;
                side--;
            }
            if (side > 0) {
                canvas.Panel(cue, new Color(48, 39, 22), Gold, Math.Min(3, side / 2));
                if (card.CombatId.Length > 0) attachmentBadges[card.CombatId] = new("", cue, 0, true);
            }
            return;
        }
        if (pose.Size.X < 300 && CardPresentation.HasCombatStats(card)
            && denseStatsBounds.TryGetValue(CardViews.Identity(card), out var denseStats)) {
            // Dense labels already paint attachment color on their measured P/T
            // rectangle. Reuse that exact text and target, including on hover.
            if (card.CombatId.Length > 0) attachmentBadges[card.CombatId] = new(
                $"{card.Power}/{card.Toughness}", denseStats.Bounds, denseStats.MeasuredWidth, true, true);
            return;
        }
        int seat = owner == match!.ViewerId ? 3 : TableLayout.SeatIndex(match, owner);
        if (compact && CardPresentation.HasCombatStats(card) && pose.Size.X < 300) {
            // A projected creature face can be shorter than its readable P/T
            // chip. Put attachment state on that existing inward-facing chip,
            // preserving both the footprint and the engine's current P/T.
            var stats = CompactStatsBounds(card, pose, seat);
            string value = $"{card.Power}/{card.Toughness}";
            canvas.Panel(stats, new Color(48, 39, 22), Gold, 3);
            canvas.CenterText(value, stats, Ink, CompactStatsSize, true);
            if (card.CombatId.Length > 0) attachmentBadges[card.CombatId] = new(value, stats, canvas.TextWidth(value, CompactStatsSize, true), true, true);
            return;
        }
        int available = Math.Max(8, Math.Min(105, pose.Bounds.Width - 4));
        int measured = canvas.TextWidth(label, .48f, true);
        compact |= measured + 8 > available;
        if (compact) {
            label = label.StartsWith("EQUIPPED", StringComparison.Ordinal) ? "EQ" : "AT";
            measured = canvas.TextWidth(label, .48f, true);
            if (measured + 4 > available || pose.Bounds.Height < 36) { label = ""; measured = 0; }
        }
        bool indicator = compact && label.Length == 0;
        int width = Math.Min(available, Math.Max(indicator ? 10 : 8, measured + (compact ? 4 : 8)));
        int height = indicator ? 10 : compact ? 16 : 22;
        int bottom = pose.Bounds.Bottom - 2;
        int top = Math.Max(pose.Bounds.Top + 1, bottom - height);
        height = Math.Min(height, Math.Max(1, bottom - top));
        // Compact creature stats face inward: the far seat uses the bottom edge,
        // your seat the top edge, and side seats the middle. Use the other edge.
        int desiredY = compact ? seat == 1 ? pose.Bounds.Top + 3 : top : pose.Bounds.Bottom - 42;
        int y = Math.Clamp(desiredY, pose.Bounds.Top + 1, top);
        int x = Math.Clamp((int)pose.Center.X - width / 2, pose.Bounds.Left + 1, Math.Max(pose.Bounds.Left + 1, pose.Bounds.Right - width - 1));
        if (indicator) {
            x = seat == 2 ? pose.Bounds.Left + 1 : pose.Bounds.Right - width - 1;
            y = pose.Bounds.Top + 1;
        }
        var bounds = new Rectangle(x, y, width, height);
        canvas.Panel(bounds, new Color(48, 39, 22), Gold, Math.Min(4, height / 2));
        if (label.Length > 0) canvas.CenterText(label, bounds, Gold, .48f, true);
        else canvas.Circle(bounds.Center.ToVector2(), 2, Gold);
        if (card.CombatId.Length > 0) attachmentBadges[card.CombatId] = new(label, bounds, measured, compact);
    }
}
