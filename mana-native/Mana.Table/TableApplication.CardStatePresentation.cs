using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly Dictionary<string, Vector2> counterAnchors = new(StringComparer.Ordinal);

    // Display resolution never replaces an action handle. Catalog artwork
    // continues to use PaintCard directly.
    private void PaintInteractionCard(Card card, Pose pose, bool selected, bool legal, bool focus = false, float opacity = 1)
    {
        var current = InteractionCards.Current(match, card);
        PaintCard(current, pose, selected, legal, focus, opacity);
        PaintAttachedCardState(current, pose);
    }

    private static string CurrentStats(Card card) => $"{card.Power?.ToString() ?? "?"}/{card.Toughness?.ToString() ?? "?"}";
    private static string CounterMarker(Card card)
    {
        var counters = CardPresentation.Counters(card);
        return counters.Length == 0 ? "" : counters[0].Value + (counters.Length > 1 ? "…" : "");
    }

    private int CounterMarkerWidth(Card card, float size) => CounterMarker(card) is { Length: > 0 } value
        ? canvas.TextWidth(value, size, true) + 20 : 0;

    private void PaintCounterMarker(Card card, Rectangle bounds, float size, int? owner = null)
    {
        string value = CounterMarker(card);
        if (value.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var accent = new Color(154, 224, 203);
        int side = Math.Min(11, Math.Max(6, bounds.Height - 5));
        int y = bounds.Center.Y - side / 2;
        canvas.Rounded(new(bounds.X + 3, y - 2, side, side), new(62, 104, 100), 2);
        canvas.Panel(new(bounds.X + 1, y, side, side), new(31, 65, 64), accent * .8f, 2);
        canvas.Circle(new(bounds.X + 1 + side / 2f, y + side / 2f), 1.2f, accent);
        canvas.Text(value, bounds.X + side + 5, bounds.Y + Math.Max(0, (bounds.Height - canvas.TextSize(value, size, 1000, 1, true).Y) / 2),
            accent, size, Math.Max(1, bounds.Width - side - 5), 1, true);
        string id = CardViews.Identity(card);
        if (owner != null && id.Length > 0) counterAnchors[id] = bounds.Center.ToVector2();
        if (owner is { } player)
            RecordBattlefieldLabel(card, player, "counter", value, bounds, canvas.TextWidth(value, size, true), bounds.Width - side - 5);
    }

    // One ribbon overlaps the face. P/T is authoritative, never derived by
    // adding counters to another projected stat value.
    private void PaintAttachedCardState(Card card, Pose pose, int? owner = null)
    {
        var counters = CardPresentation.Counters(card);
        bool creature = CardPresentation.HasCombatStats(card);
        if (!creature && counters.Length == 0 && card.Damage == 0) return;
        bool detailed = pose.Size.X >= 150;
        float size = pose.Size.X >= 300 ? 1.05f : detailed ? .86f : .62f;
        int height = pose.Size.X >= 300 ? 36 : detailed ? 30 : 23;
        string stats = creature ? CurrentStats(card) : "";
        string damage = card.Damage > 0 ? "DMG " + card.Damage : "";
        if (detailed && !creature && damage.Length == 0) height = 0;
        int Measure() => (creature ? canvas.TextWidth(stats, size, true) + 12 : 0)
            + (damage.Length > 0 ? canvas.TextWidth(damage, size, true) + 10 : 0)
            + (!detailed ? CounterMarkerWidth(card, size) : 0);
        int maximum = Math.Max(32, pose.Bounds.Width - 6);
        while (size > .375f && Measure() > maximum) size = Math.Max(.375f, size - .025f);
        int width = Math.Max(Measure(), Math.Min(maximum, creature ? 54 : 30));
        int counterRows = detailed ? Math.Min(4, counters.Length) : 0;
        int rowHeight = pose.Size.X >= 300 ? 26 : 21;
        if (counterRows > 0) rowHeight = Math.Max(rowHeight, canvas.TextSize("Ag", size * .8f, maximum, 1, true).Y + 4);
        if (counterRows > 0) width = Math.Max(width, Math.Min(maximum, counters.Take(counterRows)
            .Max(counter => canvas.TextWidth(counter.Key + " ×" + counter.Value, size * .8f, true) + 12)));
        int total = height + counterRows * rowHeight;
        int x = pose.Bounds.Right - width - 3;
        int top = Math.Max(pose.Bounds.Top + 1, pose.Bounds.Bottom - total - 3);
        var ribbon = new Rectangle(x, top, width, total);
        bool attached = card.AttachedTo != null || card.Attachments.Length > 0;
        canvas.Panel(ribbon, new(15, 25, 31), attached ? Gold : new Color(108, 133, 141), 3);
        int cursor = x + 4;
        if (!detailed && counters.Length > 0) {
            int counterWidth = CounterMarkerWidth(card, size);
            PaintCounterMarker(card, new(cursor, top + 1, counterWidth, height - 2), size, owner);
            cursor += counterWidth;
        }
        if (creature) {
            int statsWidth = canvas.TextWidth(stats, size, true) + 8;
            var bounds = new Rectangle(cursor, top, statsWidth, height);
            canvas.CenterText(stats, bounds, Ink, size, true);
            if (owner is { } player) {
                RecordBattlefieldLabel(card, player, "pt", stats, bounds, statsWidth - 8, statsWidth - 8);
                denseStatsBounds[CardViews.Identity(card)] = new(bounds, statsWidth - 8);
            }
            cursor += statsWidth;
        }
        if (damage.Length > 0) {
            int damageWidth = canvas.TextWidth(damage, size, true) + 8;
            var bounds = new Rectangle(cursor, top, damageWidth, height);
            canvas.CenterText(damage, bounds, Red, size, true);
            if (owner is { } player) RecordBattlefieldLabel(card, player, "damage", damage, bounds, damageWidth - 8, damageWidth - 8);
        }
        for (int index = 0; index < counterRows; index++) {
            var counter = counters[index];
            string label = counter.Key + " ×" + counter.Value;
            if (index == counterRows - 1 && counters.Length > counterRows) label += " …";
            var bounds = new Rectangle(x + 5, top + height + index * rowHeight, width - 10, rowHeight);
            canvas.Text(label, bounds.X, bounds.Y + 2, new(154, 224, 203), size * .8f, bounds.Width, 1, true);
            if (owner != null && index == 0 && CardViews.Identity(card) is { Length: > 0 } id) counterAnchors[id] = bounds.Center.ToVector2();
        }
    }
}
