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

        if (stateVisible) {
            // Upright labels remain inside the rotating face, including during
            // the tap animation, rather than enlarging its rectangular hit area.
            var face = BattlefieldScanLabelArea(pose);
            if (face.Width >= 36 && face.Height >= 32) {
                int nameHeight = PaintBattlefieldScanName(card, owner, face, copies);
                PaintBattlefieldScanState(card, owner, face, nameHeight, copies);
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
        var face = BattlefieldScanLabelArea(node.Pose);
        if (BattlefieldScanNameHeight(card, face, node.GroupCount) > 0) return;
        string name = card.FaceDown ? "Face-down permanent" : card.Name;
        const float size = .7f;
        int width = Math.Min(380, canvas.TextWidth(name, size, true) + 16);
        int height = canvas.TextSize(name, size, width - 16, 80, true).Y + 10;
        var bounds = new Rectangle(Math.Clamp(node.Pose.Bounds.Center.X - width / 2, 12, 1588 - width),
            Math.Clamp(node.Pose.Bounds.Top - height - 6, 58, 840 - height), width, height);
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

    private void PaintBattlefieldScanState(Card card, int owner, Rectangle face, int nameHeight, int copies)
    {
        bool creature = CardPresentation.HasCombatStats(card);
        float size = face.Width >= 100 ? .62f : .52f;
        string stats = creature ? CurrentStats(card) : "";
        string damage = card.Damage > 0 ? "DMG " + card.Damage : "";
        int Measure() => (creature ? canvas.TextWidth(stats, size, true) + 10 : 0)
            + (damage.Length > 0 ? canvas.TextWidth(damage, size, true) + 8 : 0);
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
            int measured = canvas.TextWidth(damage, size, true), width = Math.Min(right - face.Left, measured + 8);
            var bounds = new Rectangle(right - width, face.Bottom - height, width, height);
            canvas.Fill(bounds, new(25, 23, 28));
            canvas.CenterText(damage, bounds, Red, size, true);
            RecordBattlefieldLabel(card, owner, "damage", damage, bounds, measured, width - 8);
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
        var abilities = CardPresentation.Abilities(card).ToList();
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
