using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record PublicResourcePartPaint(string Key, string Name, string Amount, Rectangle Bounds, int MeasuredWidth, int AvailableWidth)
    {
        public int? Delta { get; init; }
        public double? AnimationStarted { get; init; }
        public float Opacity { get; init; } = 1;
    }
    internal sealed record PublicResourcePaint(string Label, long Total, Rectangle Bounds, PublicResourcePartPaint[] Parts, int TotalMeasuredWidth, int TotalAvailableWidth);
    private readonly Dictionary<int, PublicResourcePaint> manaPoolBadges = [];
    private PublicResourcePaint? manaPoolDetail;
    private sealed record ResourcePartLayout(PublicResourcePart Part, int X, int Row, int Width, int LabelWidth, float Size);
    private sealed record ResourceRowLayout(int Width, int Height, int HeaderHeight, ResourcePartLayout[] Parts);
    private sealed record ManaHaloPart(PublicResourcePart Part, Rectangle Bounds, int LabelWidth, float Size);
    private sealed record ManaHaloPlan(Rectangle Bounds, ManaHaloPart[] Parts, Rectangle? EmptyCue);

    private ResourceRowLayout ResourceLayout(string label, long total, PublicResourcePart[] parts, int width)
    {
        const int padding = 10, gap = 8;
        int available = width - padding * 2, x = 0, row = 0;
        var arranged = new List<ResourcePartLayout>();
        foreach (var part in parts) {
            string text = part.Amount.ToString();
            float size = .65f;
            int labelWidth = part.Symbol.Length > 0 ? 23 : canvas.TextWidth(part.Name + " ", size, true);
            while (size > .45f && labelWidth + canvas.TextWidth(text, size, true) > available) {
                size = Math.Max(.45f, size - .025f);
                labelWidth = part.Symbol.Length > 0 ? 23 : canvas.TextWidth(part.Name + " ", size, true);
            }
            int partWidth = labelWidth + canvas.TextWidth(text, size, true);
            if (x > 0 && x + partWidth > available) { row++; x = 0; }
            arranged.Add(new(part, x + padding, row, partWidth, labelWidth, size));
            x += partWidth + gap;
        }
        int header = canvas.TextWidth(label, .5f, true) + canvas.TextWidth(total.ToString(), .65f, true) + 8 > available ? 50 : 28;
        return new(width, header + (parts.Length > 0 ? (row + 1) * 25 : 0), header, arranged.ToArray());
    }

    // All public resources use a label plus complete symbol/value parts. The
    // row only presents authoritative totals; inspecting it is not a payment.
    private PublicResourcePaint DrawResourceRow(string label, long total, ResourceRowLayout layout, int x, int y, Color accent)
    {
        var bounds = new Rectangle(x, y, layout.Width, layout.Height);
        canvas.Panel(bounds, new(13, 24, 31), accent, 5);
        canvas.Text(label, x + 10, y + 5, Ink, .5f, layout.Width - 20, 1, true);
        int totalWidth = canvas.TextWidth(total.ToString(), .65f, true);
        canvas.Text(total.ToString(), x + layout.Width - totalWidth - 10, y + (layout.HeaderHeight > 28 ? 25 : 3),
            Ink, .65f, totalWidth, 1, true);
        var painted = new List<PublicResourcePartPaint>();
        foreach (var item in layout.Parts) {
            int px = x + item.X, py = y + layout.HeaderHeight - 1 + item.Row * 25;
            if (item.Part.Symbol.Length > 0) {
                var color = ResourceColor(item.Part.Symbol);
                canvas.Circle(new(px + 8, py + 10), 9, color);
                canvas.CenterText(item.Part.Symbol, new(px - 1, py, 19, 20), new(20, 26, 34), .5f, true);
            } else canvas.Text(item.Part.Name, px, py, Ink, item.Size, item.LabelWidth, 1, true);
            string amount = item.Part.Amount.ToString();
            int amountWidth = item.Width - item.LabelWidth;
            canvas.Text(amount, px + item.LabelWidth, py, Ink, item.Size, amountWidth, 1, true);
            painted.Add(new(item.Part.Key, item.Part.Name, amount, new(px, py, item.Width, 23),
                canvas.TextWidth(amount, item.Size, true), amountWidth));
        }
        return new(label, total, bounds, painted.ToArray(), totalWidth, layout.Width - 20);
    }

    private static Color ResourceColor(string symbol) => symbol switch {
        "W" => new(232, 222, 185), "U" => new(103, 169, 224), "B" => new(167, 152, 181),
        "R" => new(224, 130, 101), "G" => new(134, 186, 142), _ => new(191, 197, 203)
    };

    private ManaHaloPlan PlanManaHalo(Player player, int seat)
    {
        var parts = PublicResources.Mana(player).ToList();
        if (!preferences.ReducedMotion) {
            var known = PublicResources.Mana(player, includeEmpty: true);
            foreach (var cue in manaPresence.ForPlayer(player.Id, now).Where(cue => cue.After == 0 && parts.All(part => part.Key != cue.Symbol)))
                parts.Add(known.FirstOrDefault(part => part.Key == cue.Symbol) ?? new(cue.Symbol, cue.Symbol, "", 0));
        }
        var hero = TableLayout.Hero(seat);
        string[] symbols = ["W", "U", "B", "R", "G", "C"];
        var ordered = parts.Select(part => (Part: part, Slot: Array.IndexOf(symbols, part.Key)))
            .Select((entry, index) => (entry.Part, Slot: entry.Slot >= 0 ? entry.Slot : 6 + parts.Take(index).Count(p => !symbols.Contains(p.Key))))
            .ToArray();
        var widths = ordered.ToDictionary(entry => entry.Part.Key, entry => {
            int label = entry.Part.Symbol.Length > 0 ? 20 : canvas.TextWidth(entry.Part.Name + " ", .6f, true);
            return Math.Max(34, label + canvas.TextWidth(entry.Part.Amount.ToString(), .6f, true) + 10);
        });
        var plan = new List<ManaHaloPart>();
        int inner = seat == 3 ? 58 : 50;
        foreach (var entry in ordered) {
            int column = seat is 0 or 2 ? entry.Slot % 2 + entry.Slot / 6 * 2 : entry.Slot / 3;
            int row = seat is 0 or 2 ? entry.Slot % 6 / 2 : entry.Slot % 3;
            int width = widths[entry.Part.Key], x;
            int ColumnWidth(int col) => ordered.Where(e => (seat is 0 or 2 ? e.Slot % 2 + e.Slot / 6 * 2 : e.Slot / 3) == col)
                .Select(e => widths[e.Part.Key]).DefaultIfEmpty(0).Max();
            if (seat is 0 or 2) {
                int offset = Enumerable.Range(0, column).Sum(col => ColumnWidth(col) + 6);
                x = seat == 0 ? (int)hero.X + inner + offset : (int)hero.X - inner - offset - width;
            } else {
                bool left = column % 2 == 0;
                int offset = Enumerable.Range(0, column / 2).Sum(col => ColumnWidth(col * 2 + (left ? 0 : 1)) + 6);
                x = left ? (int)hero.X - inner - offset - width : (int)hero.X + inner + offset;
            }
            var bounds = new Rectangle(x, (int)hero.Y - 40 + row * 28, width, 24);
            int labelWidth = entry.Part.Symbol.Length > 0 ? 20 : canvas.TextWidth(entry.Part.Name + " ", .6f, true);
            plan.Add(new(entry.Part, bounds, labelWidth, .6f));
        }
        Rectangle? empty = parts.Count == 0 && seat == 3 ? new Rectangle((int)hero.X - inner - 63, (int)hero.Y - 12, 63, 24) : null;
        Rectangle envelope = plan.Select(item => item.Bounds).Aggregate(empty ?? Rectangle.Empty,
            (current, next) => current.IsEmpty ? next : Rectangle.Union(current, next));
        return new(envelope, plan.ToArray(), empty);
    }
    private void DrawPlayerMana(Player player, ManaHaloPlan halo)
    {
        var painted = new List<PublicResourcePartPaint>();
        foreach (var item in halo.Parts) {
            var bounds = item.Bounds;
            var color = ResourceColor(item.Part.Symbol);
            var cue = preferences.ReducedMotion ? null : manaPresence.For(player.Id, item.Part.Key, now);
            float progress = (float)(cue?.Progress(now) ?? 1);
            float opacity = cue is { After: 0 } ? 1 - progress : 1;
            if (cue != null) {
                float pulse = MathF.Sin(progress * MathF.PI);
                canvas.Glow(bounds.Center.ToVector2(), new(bounds.Width + 18 + pulse * 15, bounds.Height + 18 + pulse * 10),
                    color * ((cue.Delta > 0 ? .48f : .22f) * (1 - progress)));
                var rim = bounds; rim.Inflate(2 + (int)(pulse * 3), 2 + (int)(pulse * 3));
                canvas.Panel(rim, Color.Transparent, color * ((1 - progress) * .9f), 12);
            }
            canvas.Panel(bounds, new Color(17, 28, 35) * opacity, color * opacity, 10);
            if (item.Part.Symbol.Length > 0) {
                canvas.Circle(new(bounds.X + 12, bounds.Center.Y), 8, color * opacity);
                canvas.CenterText(item.Part.Symbol, new(bounds.X + 3, bounds.Y + 2, 18, 20), new Color(20, 26, 34) * opacity, .48f, true);
            } else canvas.Text(item.Part.Name, bounds.X + 5, bounds.Y + 2, Ink * opacity, item.Size, item.LabelWidth, 1, true);
            string amount = item.Part.Amount.ToString();
            int available = bounds.Width - item.LabelWidth - 10;
            canvas.Text(amount, bounds.X + item.LabelWidth + 5, bounds.Y + 1, Ink * opacity, item.Size, available, 1, true);
            painted.Add(new(item.Part.Key, item.Part.Name, amount, bounds, canvas.TextWidth(amount, item.Size, true), available)
                { Delta = cue?.Delta, AnimationStarted = cue?.Started, Opacity = opacity });
            hits.Add(new("mana-pool:" + player.Id + ":" + item.Part.Key, Scope, bounds, () => OpenManaPool(player.Id)));
        }
        if (halo.EmptyCue is { } empty) {
            canvas.Panel(empty, new(17, 28, 35), Muted, 10);
            canvas.CenterText("MANA 0", empty, Ink, .48f, true);
            hits.Add(new("mana-pool:" + player.Id, Scope, empty, () => OpenManaPool(player.Id)));
        }
        manaPoolBadges[player.Id] = new("UNSPENT MANA", PublicResources.ManaTotal(player), halo.Bounds, painted.ToArray(), 0, 0);
    }
    private void OpenManaPool(int player) { zonePlayer = player; overlay = "mana"; }

    private void DrawManaDetails()
    {
        var player = match!.Players.FirstOrDefault(p => p.Id == zonePlayer);
        if (player == null) { overlay = ""; return; }
        ModalFrame("Unspent mana · " + StateClarity.SeatName(match, player.Id), () => overlay = "");
        canvas.Text("CURRENT POOL · READ ONLY", 212, 191, Gold, .7f, 1150, 1, true);
        long total = PublicResources.ManaTotal(player);
        var layout = ResourceLayout("UNSPENT MANA", total, PublicResources.Mana(player, includeEmpty: true), 1125);
        manaPoolDetail = DrawResourceRow("UNSPENT MANA", total, layout, 212, 232, Blue);
        canvas.Text(PublicResources.ManaDetail, 212, 350, Ink, .88f, 1125, (int)Math.Ceiling(6 * preferences.TextScale));
        canvas.Text("Inspecting this pool does not spend mana or select a game action.", 212, 533, Muted, .8f, 1125, 2);
    }
}
