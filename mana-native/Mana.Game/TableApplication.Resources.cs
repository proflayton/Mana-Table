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
    private sealed record ManaHaloPlan(Rectangle Bounds, ManaHaloPart[] Parts);

    private ResourceRowLayout ResourceLayout(string label, long total, PublicResourcePart[] parts, int width)
    {
        const int padding = 10, gap = 8;
        int available = width - padding * 2, x = 0, row = 0;
        var arranged = new List<ResourcePartLayout>();
        foreach (var part in parts) {
            string text = part.Amount.ToString();
            float size = .65f;
            int labelWidth = (part.Symbol.Length > 0 ? 23 : 0) + canvas.TextWidth(part.Name + " ", size, true);
            while (size > .45f && labelWidth + canvas.TextWidth(text, size, true) > available) {
                size = Math.Max(.45f, size - .025f);
                labelWidth = (part.Symbol.Length > 0 ? 23 : 0) + canvas.TextWidth(part.Name + " ", size, true);
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
                PaintManaSymbol(item.Part.Symbol, new(px + 8, py + 10), 18);
                canvas.Text(item.Part.Name, px + 23, py, Ink, item.Size, item.LabelWidth - 23, 1, true);
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

    private void PaintManaSymbol(string symbol, Vector2 center, int side, float opacity = 1)
    {
        // Coordinates belong to Forge's existing FSkinProp mana atlas. These
        // are printed mana symbols, not color initials styled as UI buttons.
        Rectangle crop = symbol switch {
            "W" => new(412, 84, 80, 80), "U" => new(330, 84, 80, 80),
            "B" => new(166, 2, 80, 80), "R" => new(330, 2, 80, 80),
            "G" => new(166, 84, 80, 80), "C" => new(248, 2, 80, 80), _ => Rectangle.Empty
        };
        if (resources.ManaSymbols is { } image && !crop.IsEmpty && crop.Right <= image.Width && crop.Bottom <= image.Height)
            canvas.Sprite(image, center, new(side), 0, Color.White * opacity, crop);
        else {
            // Headless and missing-asset paths retain an unambiguous fallback.
            canvas.Circle(center, side / 2f, ResourceColor(symbol) * opacity);
            canvas.CenterText(symbol, new((int)center.X - side / 2, (int)center.Y - side / 2, side, side),
                new Color(20, 26, 34) * opacity, .48f, true);
        }
    }

    private ManaHaloPlan PlanManaHalo(Player player, int seat)
    {
        var parts = PublicResources.Mana(player).ToList();
        if (!preferences.ReducedMotion) {
            var known = PublicResources.Mana(player, includeEmpty: true);
            foreach (var cue in matchPresentation.ManaCues(player.Id, now).Where(cue => cue.After == 0 && parts.All(part => part.Key != cue.Symbol)))
                parts.Add(known.FirstOrDefault(part => part.Key == cue.Symbol) ?? new(cue.Symbol, cue.Symbol, "", 0));
        }
        seat = SeatDisplayIndex(player.Id);
        var hero = SeatHero(player.Id);
        string[] symbols = ["W", "U", "B", "R", "G", "C"];
        var ordered = parts.Select(part => (Part: part, Slot: Array.IndexOf(symbols, part.Key)))
            .Select((entry, index) => (entry.Part, Slot: entry.Slot >= 0 ? entry.Slot : 6 + parts.Take(index).Count(p => !symbols.Contains(p.Key))))
            .ToArray();
        var widths = ordered.ToDictionary(entry => entry.Part.Key, entry => {
            int label = entry.Part.Symbol.Length > 0 ? 20 : canvas.TextWidth(entry.Part.Name + " ", .6f, true);
            return Math.Max(34, label + canvas.TextWidth(entry.Part.Amount.ToString(), .6f, true) + 10);
        });
        var plan = new List<ManaHaloPart>();
        int inner = seat == 3 ? 102 : 50;
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
        Rectangle envelope = plan.Select(item => item.Bounds).Aggregate(Rectangle.Empty,
            (current, next) => current.IsEmpty ? next : Rectangle.Union(current, next));
        return new(envelope, plan.ToArray());
    }
    private void DrawPlayerMana(Player player, ManaHaloPlan halo)
    {
        var painted = new List<PublicResourcePartPaint>();
        foreach (var item in halo.Parts) {
            var bounds = item.Bounds;
            var color = ResourceColor(item.Part.Symbol);
            var cue = preferences.ReducedMotion ? null : matchPresentation.ManaCue(player.Id, item.Part.Key, now);
            float progress = (float)(cue?.Progress(now) ?? 1);
            float opacity = cue is { After: 0 } ? 1 - progress : 1;
            string hitId = "mana-pool:" + player.Id + ":" + item.Part.Key;
            bool hover = IsControlHovered(hitId), down = hover && pressed?.Id == hitId && CurrentHit(pressed);
            var symbol = new Vector2(bounds.X + 11, bounds.Center.Y);
            float emphasis = hover ? down ? .34f : .24f : .11f;
            if (item.Part.Symbol.Length > 0)
                canvas.Glow(symbol, new(35, 35), color * (emphasis * opacity));
            if (cue != null) {
                float pulse = MathF.Sin(progress * MathF.PI);
                canvas.Glow(symbol, new(37 + pulse * 13, 37 + pulse * 13),
                    color * ((cue.Delta > 0 ? .48f : .22f) * (1 - progress)));
            }
            if (item.Part.Symbol.Length > 0) PaintManaSymbol(item.Part.Symbol, symbol, 22, opacity);
            else canvas.Text(item.Part.Name, bounds.X + 5, bounds.Y + 2, Ink * opacity, item.Size, item.LabelWidth, 1, true);
            string amount = item.Part.Amount.ToString();
            int available = bounds.Width - item.LabelWidth - 10;
            // A spent color loses its number immediately while its orb fades.
            // Never flash a zero caption or retain an old positive count.
            if (item.Part.Amount > 0) {
                int x = bounds.X + item.LabelWidth + 5, y = bounds.Y + 1;
                canvas.Text(amount, x + 1, y + 1, Color.Black * (.8f * opacity), item.Size, available, 1, true);
                canvas.Text(amount, x, y, (hover ? Color.White : Ink) * opacity, item.Size, available, 1, true);
            }
            if (hover && item.Part.Symbol.Length > 0) canvas.Ring(symbol, down ? 10 : 12, Blue * opacity, down ? 2 : 1);
            painted.Add(new(item.Part.Key, item.Part.Name, amount, bounds, canvas.TextWidth(amount, item.Size, true), available)
                { Delta = cue?.Delta, AnimationStarted = cue?.Started, Opacity = opacity });
            hits.Add(new(hitId, Scope, bounds, new MatchIntent.OpenManaPool(player.Id)));
        }
        manaPoolBadges[player.Id] = new("UNSPENT MANA", PublicResources.ManaTotal(player), halo.Bounds, painted.ToArray(), 0, 0);
    }
    private void OpenManaPool(int player) { zonePlayer = player; overlay = "mana"; }

    private void DrawManaDetails()
    {
        var player = match!.Players.FirstOrDefault(p => p.Id == zonePlayer);
        if (player == null) return;
        ModalFrame("Unspent mana · " + StateClarity.SeatName(match, player.Id), new MatchIntent.CloseView());
        canvas.Text("CURRENT POOL · READ ONLY", 212, 191, Gold, .7f, 1150, 1, true);
        long total = PublicResources.ManaTotal(player);
        var layout = ResourceLayout("UNSPENT MANA", total, PublicResources.Mana(player, includeEmpty: true), 1125);
        manaPoolDetail = DrawResourceRow("UNSPENT MANA", total, layout, 212, 232, Blue);
        canvas.Text(PublicResources.ManaDetail, 212, 350, Ink, .88f, 1125, (int)Math.Ceiling(6 * preferences.TextScale));
        canvas.Text("Inspecting this pool does not spend mana or select a game action.", 212, 533, Muted, .8f, 1125, 2);
    }
}
