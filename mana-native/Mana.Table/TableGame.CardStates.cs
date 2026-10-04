using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private static Color AbilityColor(string keyword) => keyword switch {
        "flying" or "reach" => new(174, 218, 235),
        "deathtouch" => new(164, 204, 125),
        "lifelink" => new(238, 170, 189),
        "indestructible" or "vigilance" => new(234, 208, 140),
        "first strike" or "double strike" or "menace" or "trample" => new(234, 171, 121),
        _ => new(188, 185, 210)
    };
    private void PaintAbilityEffects(Card card, Pose pose)
    {
        if (!CardPresentation.HasAbility(card, "flying") || pose.Size.X >= 300) return;
        var center = new Vector2(pose.Center.X, pose.Bounds.Bottom + 8);
        // Quiet air trails below the lifted face; the shadow stays on the table.
        for (int i = 0; i < 3; i++) {
            float sway = preferences.ReducedMotion ? 0 : MathF.Sin((float)now * 1.5f + i) * 4;
            float width = Math.Min(62, pose.Bounds.Width * .6f) - i * 9;
            var start = center + new Vector2(-width / 2 + sway, i * 4);
            canvas.Line(start, start + new Vector2(width, -2), AbilityColor("flying") * (.32f - i * .07f), 1.5f);
        }
    }
    private void PaintStateMarkers(Card card, Pose pose, int x, int y, int seat, int owner)
    {
        if (pose.Size.X >= 300) return;
        var counters = CardPresentation.Counters(card);
        int count = Math.Min(2, counters.Length), width = seat is 0 or 2 ? 98 : Math.Clamp(pose.Bounds.Width, 80, 110);
        int counterX = seat == 0 ? x + 37 : seat == 2 ? x - 37 - width : x - width / 2;
        int counterY = seat is 0 or 2 ? y - 9 : pose.Bounds.Top - count * 27 - 8;
        if (count > 0 && tableScene?.Rank(owner, CardPresentation.IsResource(card)) is { } rank && rank.Total > rank.Capacity) {
            var reserved = new Rectangle((int)rank.Pager.X - 38, (int)rank.Pager.Y, 76, 46);
            if (reserved.Intersects(new Rectangle(counterX, counterY - 4, width, count * 27 + 4))) {
                if (seat == 0) counterX = reserved.Right + 8;
                else if (seat == 2) counterX = reserved.Left - width - 8;
                else counterY = reserved.Top - count * 27 - 8;
            }
        }
        for (int i = 0; i < count; i++) {
            var counter = counters[i];
            var bounds = new Rectangle(counterX, counterY + i * 27, width, 24);
            // A small stack of chips says "counters"; the separate numeral is
            // their count, not an additional power/toughness calculation.
            if (counter.Value > 1) canvas.Rounded(new(bounds.X - 3, bounds.Y - 3, bounds.Width, bounds.Height), new(68, 100, 100), 5);
            canvas.Panel(bounds, new(26, 47, 52), new(168, 207, 200), 5);
            canvas.Text(counter.Key, bounds.X + 5, bounds.Y + 4, Ink, .56f, bounds.Width - 32, 1, true);
            canvas.Rounded(new(bounds.Right - 26, bounds.Y + 2, 24, 20), new(181, 216, 205), 3);
            canvas.CenterText(counter.Value.ToString(), new(bounds.Right - 26, bounds.Y + 1, 24, 21), new(15, 30, 32), .65f, true);
        }
        if (counters.Length > count) canvas.Text("+" + (counters.Length - count) + " types", counterX, counterY - 22, Ink, .56f, width, 1, true);
        var states = CardPresentation.Abilities(card).ToList();
        if (card.Sick && CardPresentation.HasCombatStats(card)) states.Insert(0, "summoning sick");
        int visible = Math.Min(3, states.Count), total = visible + (states.Count > visible ? 1 : 0);
        int iconY = seat is 0 or 2 ? y + 37 : y - 15;
        for (int i = 0; i < visible; i++) PaintAbilityIcon(states[i], new(x + (i - (total - 1) / 2f) * 23, iconY));
        if (total > visible) {
            var center = new Vector2(x + (visible - (total - 1) / 2f) * 23, iconY);
            canvas.Circle(center, 11, new(22, 30, 39));
            canvas.CenterText("+" + (states.Count - visible), new((int)center.X - 11, (int)center.Y - 10, 22, 20), Ink, .5f, true);
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
        var counters = CardPresentation.Counters(card);
        var states = CardPresentation.StateLabels(card);
        var values = new List<(string Label, string Value)>();
        if (CardPresentation.HasCombatStats(card)) values.Add(("Power / toughness", $"{card.Power}/{card.Toughness}"));
        if (card.Damage > 0) values.Add(("Damage marked", card.Damage.ToString()));
        var rows = values.Concat(counters.Select(c => (Label: c.Key + " counter" + (c.Value == 1 ? "" : "s"), Value: "× " + c.Value)))
            .Concat(states.Select(s => (Label: s, Value: ""))).ToArray();
        int shown = Math.Min(16, rows.Length), height = 64 + shown * 28 + (shown < rows.Length ? 28 : 0);
        int x = pose.Bounds.Right + 17 + 265 <= 1580 ? pose.Bounds.Right + 17 : pose.Bounds.Left - 282;
        int y = Math.Clamp(pose.Bounds.Top, 70, 882 - height);
        var seat = TableLayout.LocalSeatBounds;
        if (x + 265 > seat.Left && x < seat.Right) y = Math.Min(y, seat.Top - 12 - height);
        canvas.Panel(new(x, y, 265, height), new(19, 28, 37), new(115, 145, 158), 9);
        canvas.Text("CURRENT STATE", x + 15, y + 12, Gold, .62f, 235, 1, true);
        for (int i = 0; i < shown; i++) {
            canvas.Text(rows[i].Label, x + 15, y + 44 + i * 28, Ink, .68f, rows[i].Value.Length > 0 ? 177 : 235, 1, true);
            canvas.Text(rows[i].Value, x + 198, y + 44 + i * 28, Gold, .72f, 54, 1, true);
        }
        if (shown < rows.Length) canvas.Text("Right-click for all details", x + 15, y + height - 27, Muted, .6f, 235, 1);
    }
}
