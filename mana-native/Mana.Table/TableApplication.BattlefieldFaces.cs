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
                int nameHeight = Math.Min(18, face.Height / 3);
                var nameBounds = new Rectangle(face.X, face.Y, face.Width, nameHeight);
                canvas.Fill(nameBounds, new Color(13, 24, 30) * .94f);
                int nameLeft = nameBounds.X + 4;
                if (copies > 1) {
                    string count = "×" + copies;
                    int measured = canvas.TextWidth(count, .44f, true), width = Math.Min(face.Width - 8, measured + 4);
                    var countBounds = new Rectangle(face.X + 2, face.Top, width, nameHeight);
                    canvas.CenterText(count, countBounds, Gold, .44f, true);
                    RecordBattlefieldLabel(card, owner, "copies", count, countBounds, measured, width - 4);
                    nameLeft = countBounds.Right + 3;
                }
                string name = card.FaceDown ? "Face-down permanent" : card.Name;
                float nameSize = face.Width >= 105 ? .5f : .44f;
                int nameWidth = Math.Max(1, nameBounds.Right - nameLeft - 4);
                canvas.Text(name, nameLeft, nameBounds.Y + 1, Ink, nameSize, nameWidth, 1, true);
                RecordBattlefieldLabel(card, owner, "name", name, nameBounds,
                    canvas.TextWidth(name, nameSize, true), nameWidth);
                PaintBattlefieldScanState(card, owner, face, nameHeight);
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

    private void PaintBattlefieldScanState(Card card, int owner, Rectangle face, int nameHeight)
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
        if (CounterMarker(card).Length > 0) {
            const float counterSize = .46f;
            int width = Math.Min(face.Width, CounterMarkerWidth(card, counterSize));
            var bounds = new Rectangle(face.Right - width, face.Top + nameHeight + 3, width, 18);
            canvas.Rounded(bounds, new(14, 32, 34), 3);
            PaintCounterMarker(card, bounds, counterSize, owner);
        }
        var abilities = CardPresentation.Abilities(card).ToList();
        if (card.Sick && creature) abilities.Insert(0, "summoning sick");
        if (card.PhasedOut) abilities.Insert(0, "phased out");
        for (int index = 0; index < Math.Min(2, abilities.Count); index++)
            PaintBattlefieldScanGlyph(abilities[index], new(face.Left + 8 + index * 15, face.Bottom - height - 9));
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
