using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
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
        foreach (var player in match!.Players)
        foreach (var card in player.Zone("Battlefield").Cards) {
            if (card.AttachedTo is not { } target || card.CombatId != focus && target.Id != focus) continue;
            if (tableScene?.BattlefieldCard(player.Id, CardViews.Identity(card))?.Attachment != null) {
                string linkedId = card.CombatId == focus ? target.Id : card.CombatId;
                if (cardPositions.TryGetValue(linkedId, out var linkedPose)) canvas.CardFrame(linkedPose, Gold * .7f, 2);
                continue;
            }
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
    private void PaintBattlefieldAttachment(SceneCard node, Pose pose, bool selected, bool legal)
    {
        var card = node.Card;
        PaintAbilityEffects(card, pose);
        PaintBattlefieldScanFace(card, pose, node.Owner, false, false, false, false, 1);
        if (card.PhasedOut) PaintPhasedOutline(pose);
        var edge = node.Attachment!.Edge(pose);
        bool over = pointerSurface?.Card is { } pointed && CardViews.Same(pointed, card);
        var accent = selected ? Gold : over ? HoverAccent : legal ? ActionAccent : new Color(120, 145, 148);
        if (over || selected || legal) canvas.Border(edge, accent, over || selected ? 2 : 1);
        // The exposed illustration is the attachment, not a row of controls.
        // Its complete name appears at the source on hover; click reads its face.
        bool horizontal = edge.Width > edge.Height;
        if (node.Attachment.RootOwner != node.Owner && Math.Min(edge.Width, edge.Height) >= 14) {
            var center = horizontal ? new Vector2(edge.Left + 8, edge.Center.Y) : new Vector2(edge.Center.X, edge.Top + 8);
            if (seatPortraits.TryGetValue(node.Owner, out var portraitName) && resources.Card(new Card { Name = portraitName, ArtName = portraitName }) is { } portrait)
                canvas.Portrait(portrait, center, 6, Color.White);
            else canvas.Circle(center, 5, node.Owner == match!.ViewerId ? Gold : Blue);
            canvas.Ring(center, 6, node.Owner == match!.ViewerId ? Gold : Blue, 1);
        }
        string counter = CounterMarker(card);
        if (counter.Length > 0 && Math.Min(edge.Width, edge.Height) >= 12 && Math.Max(edge.Width, edge.Height) >= 36) {
            var chip = horizontal ? new Rectangle(edge.Right - 36, edge.Y, 35, edge.Height)
                : new Rectangle(edge.X, edge.Bottom - 25, edge.Width, 24);
            canvas.Fill(chip, new Color(16, 27, 31));
            PaintCounterGlyph(horizontal ? new(chip.Left + 3, chip.Center.Y - 3, 6, 6)
                : new(edge.Center.X - 3, chip.Top + 3, 6, 6), new(154, 224, 203));
            counterAnchors[node.Id] = chip.Center.ToVector2();
            var number = horizontal ? new Rectangle(chip.X + 12, chip.Y, chip.Width - 12, chip.Height)
                : new Rectangle(chip.X, chip.Top + 11, chip.Width, 12);
            if (canvas.TextWidth(counter, .4f, true) <= number.Width - 2) {
                canvas.CenterText(counter, number, new(154, 224, 203), .4f, true);
                RecordBattlefieldLabel(card, node.Owner, "counter", counter, chip, canvas.TextWidth(counter, .4f, true), number.Width - 2);
            }
        }
        if (card.Tapped && Math.Min(edge.Width, edge.Height) >= 12 && Math.Max(edge.Width, edge.Height) >= 22) {
            var mark = new Vector2(edge.Center.X, edge.Center.Y);
            canvas.Line(mark + new Vector2(-4, -5), mark + new Vector2(3, -5), Gold, 1.5f);
            canvas.Line(mark + new Vector2(3, -5), mark + new Vector2(3, 3), Gold, 1.5f);
            canvas.Polygon([mark + new Vector2(-1, 1), mark + new Vector2(6, 1), mark + new Vector2(3, 6)], Gold);
        }
        if (TableChoices.Index(match!, card) is { } choice && decisions.Payload is OrderingPayload && decisions.Selections.Contains(choice))
            PaintChoiceOrder(choice, new(edge.Left, edge.Top, edge.Width, Math.Min(24, edge.Height)), .48f);
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
            // The shared state strip already carries the relationship color.
            // Reuse its exact P/T target rather than painting another badge.
            if (card.CombatId.Length > 0) attachmentBadges[card.CombatId] = new(
                $"{card.Power}/{card.Toughness}", denseStats.Bounds, denseStats.MeasuredWidth, true, true);
            return;
        }
        int seat = SeatDisplayIndex(owner);
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
