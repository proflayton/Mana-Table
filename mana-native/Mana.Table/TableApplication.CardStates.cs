using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void PaintCommanderTax(Card card, Pose pose)
    {
        int tax = CardPresentation.CommanderTax(card);
        if (tax <= 0) return;
        var badge = new Rectangle((int)pose.Center.X - 35, pose.Bounds.Top - 10, 70, 24);
        canvas.Panel(badge, new(47, 35, 18), Gold, 5);
        canvas.CenterText($"TAX +{tax}", badge, Gold, .52f, true);
    }
    private void PaintCommanderTaxDetails(Card card, Pose pose)
    {
        if (CardPresentation.CommanderTax(card) <= 0) return;
        int x = pose.Bounds.Right + 17 + 265 <= 1238 ? pose.Bounds.Right + 17 : pose.Bounds.Left - 282;
        int y = Math.Clamp(pose.Bounds.Top, 70, 740);
        canvas.Panel(new(x, y, 265, 122), new(19, 28, 37), Gold, 9);
        canvas.Text($"COMMANDER TAX  +{CardPresentation.CommanderTax(card)}", x + 15, y + 12, Gold, .65f, 235, 1, true);
        canvas.Text("Extra generic mana to cast from the command zone. Each previous cast from there adds 2.", x + 15, y + 43, Ink, .7f, 235, 4);
    }
    private static Color AbilityColor(string keyword) => keyword switch {
        "phased out" => new(199, 180, 239),
        "flying" or "reach" => new(174, 218, 235),
        "deathtouch" => new(164, 204, 125),
        "lifelink" => new(238, 170, 189),
        "indestructible" or "vigilance" => new(234, 208, 140),
        "first strike" or "double strike" or "menace" or "trample" => new(234, 171, 121),
        _ => new(188, 185, 210)
    };
    private void PaintAbilityEffects(Card card, Pose pose)
    {
        if ((!card.PhasedOut && !CardPresentation.HasAbility(card, "flying")) || pose.Size.X >= 300) return;
        var color = AbilityColor(card.PhasedOut ? "phased out" : "flying");
        var center = new Vector2(pose.Center.X, pose.Bounds.Bottom + 8);
        // Quiet air trails below the lifted face; the shadow stays on the table.
        for (int i = 0; i < 3; i++) {
            float sway = preferences.ReducedMotion ? 0 : MathF.Sin((float)now * 1.5f + i) * 4;
            float width = Math.Min(62, pose.Bounds.Width * .6f) - i * 9;
            var start = center + new Vector2(-width / 2 + sway, i * 4);
            canvas.Line(start, start + new Vector2(width, -2), color * (.32f - i * .07f), 1.5f);
        }
    }
    private void PaintPhasedOutline(Pose pose)
    {
        var corners = (pose with { Size = pose.Size + new Vector2(7) }).Corners;
        var color = AbilityColor("phased out");
        for (int i = 0; i < corners.Length; i++) {
            var from = corners[i]; var to = corners[(i + 1) % corners.Length];
            float length = Vector2.Distance(from, to);
            for (float offset = 0; offset < length; offset += 13)
                canvas.Line(Vector2.Lerp(from, to, offset / length), Vector2.Lerp(from, to, Math.Min(offset + 7, length) / length), color * .72f, 1.5f);
        }
    }
    private void PaintAbilityIcon(string keyword, Vector2 center)
    {
        var color = AbilityColor(keyword);
        canvas.Circle(center + new Vector2(1, 2), 12, Color.Black * .65f);
        canvas.Circle(center, 11, new(21, 28, 37));
        canvas.Ring(center, 10, color * .8f, 1.3f);
        void Line(float x1, float y1, float x2, float y2) => canvas.Line(center + new Vector2(x1, y1), center + new Vector2(x2, y2), color, 1.8f);
        switch (keyword) {
            case "phased out":
                // A card dissolving through a broken edge, distinct from flying's wings.
                Line(-6, -6, 0, -6); Line(-6, -6, -6, 6); Line(-6, 6, 0, 6);
                Line(3, -6, 6, -6); Line(6, -6, 6, -3); Line(6, 0, 6, 3); Line(3, 6, 6, 6);
                Line(-2, 0, 2, 0); break;
            case "flying":
                foreach (int sign in new[] { -1, 1 }) { Line(0, 4, sign * 7, -5); Line(sign * 7, -5, sign * 6, 1); Line(sign * 6, 1, sign * 2, 4); Line(sign * 5, -1, sign * 2, 1); }
                break;
            case "reach": Line(0, 6, 0, -6); Line(0, -6, -5, -1); Line(0, -6, 5, -1); break;
            case "first strike": case "double strike":
                Line(-4, 6, 4, -6); Line(0, -5, 4, -6); Line(4, -6, 5, -2);
                if (keyword == "double strike") Line(-7, 3, 0, -6);
                break;
            case "deathtouch":
                canvas.Circle(center + new Vector2(0, -1), 5, color);
                canvas.Circle(center + new Vector2(-2, -2), 1.3f, Color.Black); canvas.Circle(center + new Vector2(2, -2), 1.3f, Color.Black);
                Line(-3, 3, -3, 5); Line(0, 3, 0, 6); Line(3, 3, 3, 5); break;
            case "lifelink":
                canvas.Circle(center + new Vector2(-3, -2), 3.5f, color); canvas.Circle(center + new Vector2(3, -2), 3.5f, color);
                canvas.Polygon([center + new Vector2(-6, -1), center + new Vector2(6, -1), center + new Vector2(0, 6)], color); break;
            case "indestructible":
                Line(-5, -5, 5, -5); Line(-5, -5, -4, 2); Line(5, -5, 4, 2); Line(-4, 2, 0, 6); Line(4, 2, 0, 6); Line(0, -2, 0, 3); break;
            case "vigilance":
                Line(-7, 0, 0, -4); Line(0, -4, 7, 0); Line(7, 0, 0, 4); Line(0, 4, -7, 0); canvas.Circle(center, 2, color); break;
            case "menace":
                Line(-7, -3, -1, 0); Line(-1, 0, -6, 2); Line(7, -3, 1, 0); Line(1, 0, 6, 2); break;
            case "trample":
                Line(-6, -5, -1, 0); Line(-1, 0, -6, 5); Line(1, -5, 6, 0); Line(6, 0, 1, 5); break;
            case "summoning sick":
                Line(-5, -6, 5, -6); Line(-5, 6, 5, 6); Line(-4, -5, 4, 5); Line(4, -5, -4, 5); break;
            default: canvas.CenterText(CardPresentation.AbilityName(keyword)[..1], new((int)center.X - 8, (int)center.Y - 9, 16, 18), color, .52f, true); break;
        }
    }
    private void PaintStateDetails(Card card, Pose pose)
    {
        var counters = CardPresentation.Counters(card).Skip(4);
        var states = CardPresentation.StateLabels(card).Where(state => state != "Untapped");
        var values = new List<(string Label, string Value)>();
        if (CardPresentation.CommanderTax(card) > 0) values.Add(("Command zone tax", "+" + CardPresentation.CommanderTax(card)));
        var rows = values.Concat(counters.Select(c => (Label: c.Key + " counter" + (c.Value == 1 ? "" : "s"), Value: "× " + c.Value)))
            .Concat(states.Select(s => (Label: s, Value: ""))).ToArray();
        if (rows.Length == 0) return;
        int x = pose.Bounds.Right + 17 + 265 <= 1238 ? pose.Bounds.Right + 17 : pose.Bounds.Left - 282;
        var target = cardPresentation.Pinned?.ReaderTarget ?? pose;
        int y = Math.Clamp(target.Bounds.Top + 8, 70, 740);
        var seat = TableLayout.LocalSeatBounds;
        int bottom = x + 265 > seat.Left && x < seat.Right ? Math.Min(882, seat.Top - 12) : 882;
        int shown = Math.Min(rows.Length, Math.Min(16, Math.Max(0, (bottom - y - 92) / 28)));
        if (shown == 0) return;
        int height = 64 + shown * 28 + (shown < rows.Length ? 28 : 0);
        canvas.Panel(new(x, y, 265, height), new(19, 28, 37), new(115, 145, 158), 9);
        canvas.Text("ABILITIES & STATE", x + 15, y + 12, Gold, .62f, 235, 1, true);
        for (int i = 0; i < shown; i++) {
            string value = rows[i].Value;
            float valueSize = .72f;
            int valueWidth = canvas.TextWidth(value, valueSize, true);
            while (valueWidth > 235 && valueSize > .3f) {
                valueSize = Math.Max(.3f, valueSize - .025f);
                valueWidth = canvas.TextWidth(value, valueSize, true);
            }
            int labelWidth = value.Length > 0 ? Math.Max(0, 235 - valueWidth - 12) : 235;
            canvas.Text(rows[i].Label, x + 15, y + 44 + i * 28, Ink, .68f, labelWidth, 1, true);
            canvas.Text(value, x + 250 - valueWidth, y + 44 + i * 28, Gold, valueSize, valueWidth, 1, true);
        }
        if (shown < rows.Length) canvas.Text("Right-click for all details", x + 15, y + height - 27, Muted, .6f, 235, 1);
    }
}
