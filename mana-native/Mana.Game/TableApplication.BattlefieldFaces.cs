using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // A battlefield scan face uses the canonical occurrence's actual pose.
    // Reading, hand cards and interaction previews keep their full printed face.
    private void PaintBattlefieldScanFace(Card card, Pose pose, int owner, bool selected, bool legal, bool focus, bool stateVisible, int copies)
    {
        float opacity = card.PhasedOut ? .42f : 1;
        if (selected) canvas.Glow(pose.Center, pose.Size * 1.12f, Gold * .15f);
        canvas.Surface(null, pose with { Center = pose.Center + new Vector2(3, 5 + pose.Elevation), Size = pose.Size + new Vector2(4) }, Color.Black * (.35f * opacity));
        canvas.Surface(null, pose, new Color(29, 44, 48) * opacity);
        if (card.FaceDown) PaintBack(pose, opacity);
        else if (resources.Card(card) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                Math.Max(1, (int)(image.Width * .76f)), Math.Max(1, (int)(image.Height * .38f)));
            // Cover the scan face without stretching the original illustration.
            float aspect = pose.Size.X / Math.Max(1, pose.Size.Y);
            if (crop.Width / (float)crop.Height > aspect) {
                int width = Math.Max(1, (int)(crop.Height * aspect));
                crop.X += (crop.Width - width) / 2; crop.Width = width;
            } else {
                int height = Math.Max(1, (int)(crop.Width / aspect));
                crop.Y += (crop.Height - height) / 2; crop.Height = height;
            }
            canvas.Sprite(image, pose.Center, pose.Size, pose.Angle, Color.White * opacity, crop);
        }
        canvas.CardFrame(pose, new Color(98, 122, 126) * opacity, 1);

        var face = BattlefieldScanLabelArea(pose);
        bool tappedCue = stateVisible && card.Tapped && PaintBattlefieldTapCue(card, owner, face, copies, opacity);
        if (stateVisible) {
            // Upright labels remain inside the rotating face, including during
            // the tap animation, rather than enlarging its rectangular hit area.
            if (face.Width >= 36 && face.Height >= 32) {
                int nameHeight = PaintBattlefieldScanName(card, owner, face, copies);
                PaintBattlefieldScanState(card, owner, face, nameHeight, copies, tappedCue);
                int statsHeight = CardPresentation.HasCombatStats(card) || card.Damage > 0 ? Math.Min(22, face.Height / 3) : 0;
                if (copies > 1 && face.Top + nameHeight + 21 <= face.Bottom - statsHeight) {
                    string count = "×" + copies;
                    int measured = canvas.TextWidth(count, .44f, true), width = measured + 6;
                    if (width <= face.Width) {
                        var countBounds = new Rectangle(face.X, face.Top + nameHeight + 3, width, 18);
                        canvas.Rounded(countBounds, new(13, 24, 30), 3);
                        canvas.CenterText(count, countBounds, Gold, .44f, true);
                        RecordBattlefieldLabel(card, owner, "copies", count, countBounds, measured, width - 6);
                    }
                }
            }
        }
        if (legal || selected) {
            if (selected) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(4) }, Gold, 3);
            else canvas.CardAction(pose, ActionAccent, 1);
        }
        if (card.Attacking || card.Blocking)
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(3) }, card.Attacking ? Red : Blue, 2);
        if (focus) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(3) }, Ink * .5f, 2);
        if (stateVisible) PaintPreparedMarker(card, pose, owner);
    }

    private bool PaintBattlefieldTapCue(Card card, int owner, Rectangle face, int copies, float opacity)
    {
        // Use the remaining illustration, rather than a tiny metadata glyph.
        // Labels are painted afterward so current stats, counters and the full
        // name remain unobscured. This treatment never expands an input region.
        bool labels = face.Width >= 36 && face.Height >= 32;
        bool sharedName = tableScene?.BattlefieldCard(owner, CardViews.Identity(card))?.StudyFamily is { CohortCount: > 1 };
        int caption = labels && !sharedName ? BattlefieldScanNameHeight(card, face, copies) : 0;
        int stats = labels && (CardPresentation.HasCombatStats(card) || card.Damage > 0) ? Math.Min(22, face.Height / 3) : 0;
        bool badges = copies > 1 || CounterMarker(card).Length > 0 || card.Sick || card.PhasedOut
            || CardPresentation.Abilities(card).Any(ability => ability != "tapped");
        int top = face.Top + caption + (labels && badges ? 21 : 0) + 2;
        var art = new Rectangle(face.Left + 2, top, face.Width - 4, face.Bottom - stats - top - 2);
        if (art.Width < 18 || art.Height < 14) return false;

        var center = art.Center.ToVector2();
        float width = Math.Min(64, art.Width - 4f), height = Math.Min(48, art.Height - 4f);
        float stroke = Math.Clamp(Math.Min(art.Width, art.Height) * .09f, 1.9f, 3.2f);
        float headHeight = Math.Clamp(height * .45f, 5, 16), headHalfWidth = Math.Clamp(width * .2f, 4, 10);
        float left = center.X - width / 2, topY = center.Y - height / 2;
        var tip = new Vector2(center.X + width / 2 - headHalfWidth, center.Y + height / 2);
        var neck = tip - new Vector2(0, headHeight);
        float radius = Math.Min(width * .22f, height * .25f);
        var bend = new Vector2(tip.X - radius, topY + radius);
        Vector2 At(float angle) => bend + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        var outline = new Color(9, 18, 22) * (.93f * opacity);
        var ink = new Color(242, 231, 196) * (.9f * opacity);
        void Arm(Color color, float lineWidth)
        {
            // An open right-angle arrow reads as tapping even on a narrow land
            // cohort; a nearly complete loop reads as another circular badge.
            const int segments = 6;
            var previous = At(-MathF.PI / 2);
            canvas.Line(new(left, topY), previous, color, lineWidth);
            for (int index = 1; index <= segments; index++) {
                var next = At(-MathF.PI / 2 + MathF.PI / 2 * index / segments);
                canvas.Line(previous, next, color, lineWidth); previous = next;
            }
            canvas.Line(previous, neck + new Vector2(0, 1), color, lineWidth);
        }
        Arm(outline, stroke + 2.6f);
        canvas.Polygon([tip + new Vector2(0, 1.3f), neck + new Vector2(-headHalfWidth - 1.3f, -1), neck + new Vector2(headHalfWidth + 1.3f, -1)], outline);
        Arm(ink, stroke);
        canvas.Polygon([tip, neck - new Vector2(headHalfWidth, 0), neck + new Vector2(headHalfWidth, 0)], ink);
        return true;
    }

    private const float BattlefieldNameSize = .5f;

    private int BattlefieldScanNameHeight(Card card, Rectangle face, int copies)
    {
        if (face.Width < 36 || face.Height < 32) return 0;
        string name = card.FaceDown ? "Face-down permanent" : card.Name;
        if (name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => canvas.TextWidth(word, BattlefieldNameSize, true) > face.Width - 8)) return 0;
        int height = canvas.TextSize(name, BattlefieldNameSize, face.Width - 8, 80, true).Y + 4;
        int twoLines = canvas.TextSize("Ag", BattlefieldNameSize, 2000, 80, true).Y * 2 + 4;
        int statsHeight = CardPresentation.HasCombatStats(card) || card.Damage > 0 ? 22 : 0;
        int badgeHeight = copies > 1 || CounterMarker(card).Length > 0 ? 21 : 0;
        return height <= twoLines && height + statsHeight + badgeHeight + 2 <= face.Height ? height : 0;
    }

    private int PaintBattlefieldScanName(Card card, int owner, Rectangle face, int copies)
    {
        // Never turn a card's identity into a fragment or shrink its font to fit.
        // Small overview faces retain art/state; pointing at one reveals its
        // complete name at the same source without moving the card's hit target.
        if (tableScene?.BattlefieldCard(owner, CardViews.Identity(card))?.StudyFamily is { CohortCount: > 1 }) return 0;
        int height = BattlefieldScanNameHeight(card, face, copies);
        if (height == 0) return 0;
        var bounds = new Rectangle(face.X, face.Y, face.Width, height);
        canvas.Fill(bounds, new Color(13, 24, 30) * .94f);
        string name = card.FaceDown ? "Face-down permanent" : card.Name;
        canvas.Text(name, face.X + 4, face.Y + 2, Ink, BattlefieldNameSize, face.Width - 8, 80, true);
        RecordBattlefieldLabel(card, owner, "name", name, bounds,
            canvas.TextSize(name, BattlefieldNameSize, face.Width - 8, 80, true).X, face.Width - 8);
        return height;
    }

    private void DrawStudyBattlefieldName()
    {
        if (!TableStudyEnabled || OverlayOpen || dragging || readingId != null
            || HitAt(pointer) is not { Card: { } card, Zone: "Battlefield", PlayerId: { } owner }
            || tableScene?.BattlefieldCard(owner, CardViews.Identity(card)) is not { ScanFace: true } node) return;
        var displayed = boardView.Apply(node.Pose);
        var face = BattlefieldScanLabelArea(displayed);
        if (node.StudyFamily is { CohortCount: > 1 } family ? StudyFamilyNameVisible(family)
            : BattlefieldScanNameHeight(card, face, node.GroupCount) > 0) return;
        string name = card.FaceDown ? "Face-down permanent" : card.Name;
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
        var bounds = pose.Bounds;
        float cosine = Math.Abs(MathF.Cos(pose.Angle)), sine = Math.Abs(MathF.Sin(pose.Angle));
        float scale = Math.Min(1, Math.Min(pose.Size.X / Math.Max(1, cosine * bounds.Width + sine * bounds.Height),
            pose.Size.Y / Math.Max(1, sine * bounds.Width + cosine * bounds.Height)));
        int width = Math.Max(0, (int)(bounds.Width * scale) - 6);
        int height = Math.Max(0, (int)(bounds.Height * scale) - 6);
        return new((int)pose.Center.X - width / 2, (int)pose.Center.Y - height / 2, width, height);
    }

    private void PaintBattlefieldScanState(Card card, int owner, Rectangle face, int nameHeight, int copies, bool tappedCue)
    {
        bool creature = CardPresentation.HasCombatStats(card);
        float size = face.Width >= 100 ? .62f : .52f;
        string stats = creature ? CurrentStats(card) : "";
        string damage = card.Damage > 0 ? card.Damage.ToString() : "";
        int Measure() => (creature ? canvas.TextWidth(stats, size, true) + 10 : 0)
            + (damage.Length > 0 ? canvas.TextWidth(damage, size, true) + 18 : 0);
        while (size > .375f && Measure() > face.Width) size = Math.Max(.375f, size - .025f);
        int height = Math.Min(22, face.Height / 3), right = face.Right;
        if (creature) {
            int measured = canvas.TextWidth(stats, size, true), width = Math.Min(face.Width, measured + 10);
            var bounds = new Rectangle(right - width, face.Bottom - height, width, height);
            bool attached = card.AttachedTo != null || card.Attachments.Length > 0;
            canvas.Panel(bounds, new(13, 24, 30), attached ? Gold : new Color(106, 135, 143), 3);
            canvas.CenterText(stats, bounds, Ink, size, true);
            RecordBattlefieldLabel(card, owner, "pt", stats, bounds, measured, width - 10);
            denseStatsBounds[CardViews.Identity(card)] = new(bounds, measured);
            right = bounds.Left;
        }
        if (damage.Length > 0 && right > face.Left) {
            int measured = canvas.TextWidth(damage, size, true), width = measured + 18;
            if (width <= right - face.Left) {
                var bounds = new Rectangle(right - width, face.Bottom - height, width, height);
                canvas.Fill(bounds, new(25, 23, 28));
                var center = new Vector2(bounds.Left + 6, bounds.Center.Y);
                canvas.Line(center + new Vector2(-3, 4), center + new Vector2(3, -4), Red, 2);
                canvas.Line(center + new Vector2(-3, -1), center + new Vector2(0, 2), Red, 1.5f);
                canvas.CenterText(damage, new(bounds.Left + 12, bounds.Top, bounds.Width - 12, bounds.Height), Red, size, true);
                RecordBattlefieldLabel(card, owner, "damage", damage, bounds, measured, width - 18);
            }
        }
        bool badgeRoom = face.Top + nameHeight + 21 <= face.Bottom - (creature || damage.Length > 0 ? height : 0);
        int copyWidth = copies > 1 ? canvas.TextWidth("×" + copies, .44f, true) + 10 : 0;
        int counterWidth = 0;
        if (CounterMarker(card).Length > 0 && badgeRoom) {
            const float counterSize = .46f;
            int width = CounterMarkerWidth(card, counterSize);
            if (width + copyWidth <= face.Width) {
                var bounds = new Rectangle(face.Right - width, face.Top + nameHeight + 3, width, 18);
                canvas.Rounded(bounds, new(14, 32, 34), 3);
                PaintCounterMarker(card, bounds, counterSize, owner);
                counterWidth = width;
            }
        }
        var abilities = CardPresentation.Abilities(card).Where(ability => ability != "tapped" || !tappedCue).ToList();
        if (card.Tapped && !tappedCue && !abilities.Contains("tapped")) abilities.Insert(0, "tapped");
        if (card.Sick && creature) abilities.Insert(0, "summoning sick");
        if (card.PhasedOut) abilities.Insert(0, "phased out");
        for (int index = 0; badgeRoom && index < Math.Min(2, abilities.Count)
            && copyWidth + 14 + index * 15 <= face.Width - counterWidth; index++)
            PaintBattlefieldScanGlyph(abilities[index], new(face.Left + copyWidth + 8 + index * 15, face.Top + nameHeight + 12));
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
