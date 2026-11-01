using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record CardFallbackLayout(Rectangle Name, float NameSize, IReadOnlyList<Rectangle> Reserved);
    private sealed record BattlefieldScanLayout(Rectangle Face, float StatsSize, Rectangle StatsRow,
        Rectangle BadgeRow, bool BadgeRoom, CardFallbackLayout? Fallback);
    // Every battlefield occurrence keeps the same original printing used in
    // the hand and reader. Current engine state supplements that printed face.
    private void PaintBattlefieldScanFace(Card card, Pose pose, int owner, bool selected, bool legal, bool focus, bool stateVisible, int copies)
    {
        float opacity = (card.PhasedOut ? .42f : 1) * (card.Tapped ? .72f : 1);
        var layout = stateVisible ? MeasureBattlefieldScanLayout(card, pose, copies,
            !card.FaceDown && resources.Card(card) == null) : null;
        var identity = PaintCard(card, pose, selected, legal, focus, opacity, layout?.Fallback);
        if (layout?.Fallback is { } fallbackLayout && identity is { } name)
            RecordBattlefieldLabel(card, owner, "identity", card.Name, name,
                canvas.TextSize(card.Name, fallbackLayout.NameSize, name.Width, 80, true).X, name.Width);
        if (!stateVisible) return;

        var face = BattlefieldScanLabelArea(pose);
        // Keep the printing's title band clear. State stays upright within the
        // actual turned card, without replacing its name or covering its art.
        int title = Math.Clamp((int)(face.Height * .14f), 0, 18);
        face = new(face.X, face.Y + title, face.Width, Math.Max(0, face.Height - title));
        if (face.Width >= 24 && face.Height >= 24)
            PaintBattlefieldScanState(card, owner, layout!, copies);
        if (copies > 1) {
            string count = "\u00d7" + copies;
            int measured = canvas.TextWidth(count, .44f, true), width = measured + 6;
            var bounds = new Rectangle(layout!.BadgeRow.Left, layout.BadgeRow.Top, width,
                canvas.TextSize(count, .44f, 1000, 1, true).Y);
            canvas.Rounded(bounds, new(13, 24, 30), 3);
            canvas.CenterText(count, bounds, Gold, .44f, true);
            RecordBattlefieldLabel(card, owner, "copies", count, bounds, measured, width - 6);
        }
        PaintPreparedMarker(card, pose, owner);
    }

    private void DrawStudyBattlefieldName()
    {
        if (!TableStudyEnabled || OverlayOpen || dragging || readingId != null
            || HitAt(pointer) is not { Card: { } card, Zone: "Battlefield", PlayerId: { } owner }
            || tableScene?.BattlefieldCard(owner, CardViews.Identity(card)) is not { ScanFace: true } node) return;
        var displayed = boardView.Apply(node.Pose);
        if (node.Attachment == null && node.StudyFamily is { CohortCount: > 1 } family && StudyFamilyNameVisible(family)) return;
        string name = card.FaceDown ? "Face-down permanent" : card.Name;
        if (node.Attachment is { } attached && attached.RootOwner != owner)
            name += " · " + match!.Players.First(player => player.Id == owner).Name;
        const float size = .7f;
        int width = Math.Min(380, canvas.TextWidth(name, size, true) + 16);
        int height = canvas.TextSize(name, size, width - 16, 80, true).Y + 10;
        var bounds = new Rectangle(Math.Clamp(displayed.Bounds.Center.X - width / 2, 12, 1588 - width),
            Math.Clamp(displayed.Bounds.Top - height - 6, 58, 840 - height), width, height);
        canvas.Panel(bounds, new(13, 24, 30), HoverAccent, 4);
        canvas.Text(name, bounds.X + 8, bounds.Y + 5, Ink, size, bounds.Width - 16, 80, true);
        // This is a read-only identity cue, not a second card or action surface.
    }

    private static Rectangle BattlefieldScanLabelArea(Pose pose)
    {
        return pose.InteriorBounds(1);
    }

    private BattlefieldScanLayout MeasureBattlefieldScanLayout(Card card, Pose pose, int copies, bool fallback)
    {
        bool creature = CardPresentation.HasCombatStats(card);
        var outline = pose.Bounds;
        // The printing's title occupies the narrow upper edge. Allocate the
        // remaining real quadrilateral by measured rows, rather than shrinking
        // both axes of one rectangle and guessing badge heights.
        int top = outline.Top + 1 + Math.Min(18, (int)(outline.Height * .06f));
        int bottom = outline.Bottom - 1;
        var face = pose.InteriorBand(top, bottom, 1);
        float size = face.Width >= 100 ? .62f : .52f;
        string stats = creature ? CurrentStats(card) : "";
        string damage = card.Damage > 0 ? card.Damage.ToString() : "";
        const float counterSize = .46f;
        bool hasCounter = CounterMarker(card).Length > 0;
        int counterHeight = hasCounter ? Math.Max(18, canvas.TextSize(CounterMarker(card), counterSize, 1000, 1, true).Y) : 0;
        int essentialBadgeHeight = Math.Max(counterHeight, copies > 1 ? canvas.TextSize("×" + copies, .44f, 1000, 1, true).Y : 0);
        int Height() => creature || damage.Length > 0
            ? Math.Max(canvas.TextSize(stats, size, 1000, 1, true).Y,
                canvas.TextSize(damage, size, 1000, 1, true).Y) : 0;
        int Measure() => (creature ? canvas.TextWidth(stats, size, true) + 4 : 0)
            + (damage.Length > 0 ? canvas.TextWidth(damage, size, true) + 18 : 0);
        while (size > .375f && (Measure() > face.Width || Height() + essentialBadgeHeight + (essentialBadgeHeight > 0 ? 1 : 0) > face.Height))
            size = Math.Max(.375f, size - .025f);
        int height = Height();
        var statsRow = pose.InteriorBand(bottom - height, bottom, 1);
        CardFallbackLayout? identity = null;
        if (fallback) {
            float nameSize = pose.Size.X > 100 ? .67f : .47f;
            int nameTop = outline.Top + 5;
            var nameArea = pose.InteriorBand(nameTop, bottom - height - (essentialBadgeHeight > 0 ? essentialBadgeHeight + 3 : 1), 4);
            var name = Rectangle.Empty;
            if (!nameArea.IsEmpty && card.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .All(word => canvas.TextWidth(word, nameSize, true) <= nameArea.Width)) {
                var measured = canvas.TextSize(card.Name, nameSize, nameArea.Width, 80, true);
                if (measured.Y <= nameArea.Height) name = new(nameArea.X, nameArea.Y, nameArea.Width, measured.Y);
            }
            if (!name.IsEmpty) top = Math.Max(top, name.Bottom + 2);
            identity = new(name, nameSize, []);
        }
        var badgeRow = pose.InteriorBand(top, top + Math.Max(18, essentialBadgeHeight), 1);
        bool badgeRoom = !badgeRow.IsEmpty && badgeRow.Bottom + 1 <= bottom - height;
        bool hasBadge = essentialBadgeHeight > 0 || CardPresentation.Abilities(card).Any(ability => ability != "tapped")
            || card.Sick && creature || card.PhasedOut;
        if (identity != null) identity = identity with { Reserved = new[] { statsRow, badgeRoom && hasBadge ? badgeRow : Rectangle.Empty }.Where(bounds => !bounds.IsEmpty).ToArray() };
        return new(face, size, statsRow, badgeRow, badgeRoom, identity);
    }

    private void PaintBattlefieldScanState(Card card, int owner, BattlefieldScanLayout layout, int copies)
    {
        bool creature = CardPresentation.HasCombatStats(card);
        string stats = creature ? CurrentStats(card) : "";
        string damage = card.Damage > 0 ? card.Damage.ToString() : "";
        float size = layout.StatsSize;
        var statsRow = layout.StatsRow;
        int height = statsRow.Height, bottom = statsRow.Bottom;
        int right = statsRow.Right;
        if (creature) {
            int measured = canvas.TextWidth(stats, size, true), width = Math.Min(statsRow.Width, measured + 4);
            var bounds = new Rectangle(right - width, bottom - height, width, height);
            bool attached = card.AttachedTo != null || card.Attachments.Length > 0;
            canvas.Panel(bounds, new(13, 24, 30), attached ? Gold : new Color(106, 135, 143), 3);
            canvas.CenterText(stats, bounds, Ink, size, true);
            RecordBattlefieldLabel(card, owner, "pt", stats, bounds, measured, width - 4);
            battlefieldStatsBounds[CardViews.Identity(card)] = new(bounds, measured);
            right = bounds.Left;
        }
        if (damage.Length > 0 && right > statsRow.Left) {
            int measured = canvas.TextWidth(damage, size, true), width = measured + 18;
            if (width <= right - statsRow.Left) {
                var bounds = new Rectangle(right - width, bottom - height, width, height);
                canvas.Fill(bounds, new(25, 23, 28));
                var center = new Vector2(bounds.Left + 6, bounds.Center.Y);
                canvas.Line(center + new Vector2(-3, 4), center + new Vector2(3, -4), Red, 2);
                canvas.Line(center + new Vector2(-3, -1), center + new Vector2(0, 2), Red, 1.5f);
                canvas.CenterText(damage, new(bounds.Left + 12, bounds.Top, bounds.Width - 12, bounds.Height), Red, size, true);
                RecordBattlefieldLabel(card, owner, "damage", damage, bounds, measured, width - 18);
            }
        }
        var badgeRow = layout.BadgeRow;
        bool badgeRoom = layout.BadgeRoom;
        int copyWidth = copies > 1 ? canvas.TextWidth("×" + copies, .44f, true) + 10 : 0;
        int counterWidth = 0;
        if (CounterMarker(card).Length > 0 && badgeRoom) {
            const float counterSize = .46f;
            int width = CounterMarkerWidth(card, counterSize);
            if (width + copyWidth <= badgeRow.Width) {
                var bounds = new Rectangle(badgeRow.Right - width, badgeRow.Top, width, badgeRow.Height);
                canvas.Rounded(bounds, new(14, 32, 34), 3);
                PaintCounterMarker(card, bounds, counterSize, owner);
                counterWidth = width;
            }
        }
        var abilities = CardPresentation.Abilities(card).Where(ability => ability != "tapped").ToList();
        if (card.Sick && creature) abilities.Insert(0, "summoning sick");
        if (card.PhasedOut) abilities.Insert(0, "phased out");
        for (int index = 0; badgeRoom && index < Math.Min(2, abilities.Count)
            && copyWidth + 14 + index * 15 <= badgeRow.Width - counterWidth; index++)
        {
            var center = new Vector2(badgeRow.Left + copyWidth + 8 + index * 15, badgeRow.Center.Y);
            PaintBattlefieldScanGlyph(abilities[index], center);
            RecordBattlefieldLabel(card, owner, "ability", abilities[index], new((int)center.X - 6, (int)center.Y - 6, 12, 12), 12, 12);
        }
    }

    private void PaintBattlefieldScanGlyph(string ability, Vector2 center)
    {
        var color = AbilityColor(ability);
        canvas.Circle(center, 6, new(15, 27, 32));
        void Line(float x1, float y1, float x2, float y2) => canvas.Line(center + new Vector2(x1, y1), center + new Vector2(x2, y2), color, 1.2f);
        switch (ability) {
            case "tapped":
                canvas.Ring(center, 3.5f, color, 1.3f, -MathF.PI * .85f, MathF.PI * .65f);
                Line(3, 0, 1, -2); Line(3, 0, 4, -3); break;
            case "flying": Line(-4, -3, 0, 3); Line(0, 3, 4, -3); Line(-4, -3, -4, 1); Line(4, -3, 4, 1); break;
            case "reach": Line(0, 4, 0, -4); Line(0, -4, -3, -1); Line(0, -4, 3, -1); break;
            case "summoning sick": Line(-3, -4, 3, -4); Line(-3, 4, 3, 4); Line(-3, -3, 3, 3); Line(3, -3, -3, 3); break;
            case "phased out": Line(-4, -4, -4, 4); Line(-4, -4, -1, -4); Line(-4, 4, -1, 4); Line(2, -4, 4, -4); Line(4, -4, 4, -1); Line(4, 2, 4, 4); break;
            case "lifelink": canvas.Circle(center + new Vector2(-2, -1), 2.2f, color); canvas.Circle(center + new Vector2(2, -1), 2.2f, color); canvas.Polygon([center + new Vector2(-4, 0), center + new Vector2(4, 0), center + new Vector2(0, 4)], color); break;
            case "vigilance": Line(-4, 0, 0, -3); Line(0, -3, 4, 0); Line(4, 0, 0, 3); Line(0, 3, -4, 0); canvas.Circle(center, 1, color); break;
            default: Line(0, -3, 3, 0); Line(3, 0, 0, 3); Line(0, 3, -3, 0); Line(-3, 0, 0, -3); break;
        }
    }
}
