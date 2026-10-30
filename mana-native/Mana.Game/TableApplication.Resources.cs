using Mana.Contracts;
using Mana.Magic;
using Mana.Planning;
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
        if (CurrentManaReading && player.Id == zonePlayer) return;
        var painted = new List<PublicResourcePartPaint>();
        foreach (var item in halo.Parts) {
            var bounds = item.Bounds;
            var color = ResourceColor(item.Part.Symbol);
            var cue = preferences.ReducedMotion ? null : matchPresentation.ManaCue(player.Id, item.Part.Key, now);
            float progress = (float)(cue?.Progress(now) ?? 1);
            float opacity = cue is { After: 0 } ? 1 - progress : 1;
            string hitId = "mana-pool:" + player.Id + ":" + item.Part.Key;
            var use = ManaPoolUse(player, item.Part);
            bool spend = use.Spend;
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
            if (spend) {
                canvas.Ring(symbol, 14, new Color(135, 221, 194) * opacity, 1.5f);
            }
            painted.Add(new(item.Part.Key, item.Part.Name, amount, bounds, canvas.TextWidth(amount, item.Size, true), available)
                { Delta = cue?.Delta, AnimationStarted = cue?.Started, Opacity = opacity });
            hits.Add(new(hitId, Scope, bounds, spend ? new MatchIntent.SpendFloatingMana(item.Part.Key) : new MatchIntent.OpenManaPool(player.Id)));
        }
        manaPoolBadges[player.Id] = new("UNSPENT MANA", PublicResources.ManaTotal(player), halo.Bounds, painted.ToArray(), 0, 0);
    }

    private (bool Spend, string Hint) ManaPoolUse(Player player, PublicResourcePart part)
    {
        if (match is not { Decision.Payload: PaymentPayload payment } state || part.Amount <= 0) return (false, "");
        bool payer = player.Id == (payment.PayerId ?? state.ViewerId);
        bool usable = payment.UsablePoolManaColors?.Contains(part.Key) == true;
        // Spending retains the existing legacy payer fallback. A negative
        // explanation needs explicit current facts; absence is not exclusion.
        bool known = !busy && !session.NeedsRecovery && state.Status == "playing" && state.Viewer is { Eliminated: false }
            && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
            && player.Id == state.ViewerId && payment.PayerId == player.Id && payment.UsablePoolManaColors != null;
        return (payer && usable, payer && usable ? "Spend" : known ? "Not usable for the remaining cost" : "");
    }

    private void DrawManaPoolWitness(ManaJourneyRead journey, Player viewer)
    {
        if (journey.Payment || journey.Plan is not { Status: ManaFundingStatus.Funded } plan
            || dragging || busy || session.NeedsRecovery || viewer.Eliminated
            || match is not { Decision.Payload: PriorityPayload } state
            || state.DecisionOwnerId != viewer.Id || state.PriorityPlayerId != viewer.Id
            || !viewer.Zone("Hand").Cards.Any(card => !card.FaceDown && card.Key == journey.Source.Key && CardViews.Same(card, journey.Source))
            || !ReaderActionsMatch(journey.Source) || !manaPoolBadges.TryGetValue(viewer.Id, out var pool)) return;
        // Portrait pips were composed before this frame's source read. Add only
        // its exact nominal pool witness now, keeping actual counts and hits.
        foreach (var steps in plan.Steps.OfType<ManaFundingStep.Pool>().Where(step => step.Amount > 0).GroupBy(step => step.Color)) {
            if (viewer.Mana.GetValueOrDefault(steps.Key) < steps.Sum(step => (long)step.Amount)
                || pool.Parts.FirstOrDefault(part => part.Key == steps.Key) is not { } pip) continue;
            canvas.Ring(new(pip.Bounds.X + 11, pip.Bounds.Center.Y), 14, new Color(135, 221, 194) * .65f, 1);
        }
    }

    private void DrawManaPoolHover()
    {
        if (!InputReady || OverlayOpen || CardCollectionOpen || match is not { } state) return;
        // Compose only after this frame's mana bill, funding annotations and
        // readers. No prior-frame caption or resource geometry is retained.
        foreach (var player in state.Players) foreach (var part in PublicResources.Mana(player)) {
            if (!IsControlHovered("mana-pool:" + player.Id + ":" + part.Key)) continue;
            var halo = PlanManaHalo(player, SeatDisplayIndex(player.Id));
            DrawManaPoolHint(part, halo.Bounds, ManaPoolUse(player, part).Hint);
            return;
        }
    }

    private void DrawManaPoolHint(PublicResourcePart part, Rectangle halo, string guidance)
    {
        string heading = part.Name + " mana · " + part.Amount;
        const float headingSize = .60f, guidanceSize = .56f;
        int width = Math.Clamp(Math.Max(canvas.TextWidth(heading, headingSize, true), canvas.TextWidth(guidance, guidanceSize)) + 20, 140, 420);
        int headingHeight = canvas.TextSize(heading, headingSize, width - 20, 80, true).Y;
        int guidanceHeight = canvas.TextSize(guidance, guidanceSize, width - 20, 80).Y;
        int height = headingHeight + (guidanceHeight > 0 ? guidanceHeight + 4 : 0) + 16;
        // Reuse the current table's reserved portrait, pip and control geometry.
        // This is a measured hover caption, without another input surface.
        var reserved = new List<Rectangle> { decisionPanelBounds, sourceReaderActionsBounds };
        if (hoverPreview is { } reader) reserved.Add(reader);
        foreach (var source in manaSourcePaint) {
            reserved.Add(source.Bounds);
            if (!source.OutputBounds.IsEmpty) reserved.Add(source.OutputBounds);
        }
        if (manaJourneyPaint is { } bill) { reserved.Add(bill.Bounds); reserved.Add(bill.SourceBounds); }
        var room = DecisionActionArea(halo, width, height, reserveBattlefield: true,
            additionalReservations: reserved, minimumWidth: width, boundedWorkspace: true);
        if (room.Width < width || room.Height < height) return;
        var area = new Rectangle(room.X, room.Y, width, height);
        canvas.Rounded(area, new Color(8, 17, 23) * .94f, 4);
        canvas.Text(heading, area.X + 10, area.Y + 8, Ink, headingSize, width - 20, 80, true);
        if (guidanceHeight > 0)
            canvas.Text(guidance, area.X + 10, area.Y + 12 + headingHeight, Muted, guidanceSize, width - 20, 80);
    }

    private void OpenManaPool(int player)
    {
        if (chatOpen) ToggleChat();
        zonePlayer = player; overlay = "mana";
    }

    private Player? ManaReadingPlayer => overlay == "mana" && !session.NeedsRecovery
        && match is { Status: "playing", Viewer: { Eliminated: false } } state
            ? state.Players.FirstOrDefault(player => player.Id == zonePlayer && !player.Eliminated) : null;
    private bool CurrentManaReading => ManaReadingPlayer != null && !StartupVisible && !updatesOpen && !ActivityOpen && !ExperienceOpen;

    private void ReconcileManaReading(GameSnapshot? previous, GameSnapshot? state)
    {
        if (overlay == "mana" && (!CurrentManaReading || previous?.Id != state?.Id
            || previous?.ViewerId != state?.ViewerId || previous?.Decision?.Id != state?.Decision?.Id)) overlay = "";
    }

    // This runs immediately before publication, after every late hit producer.
    // OverlayOpen remains authoritative to ResolveHit and command admission.
    private void IsolateManaReadingInput()
    {
        if (!CurrentManaReading) return;
        var close = hits.LastOrDefault(hit => hit.Intent is MatchIntent.CloseView);
        hits.Clear();
        hits.Add(new("mana-reading-surface", Scope, new(0, 0, 1600, DesignHeight), UiIntent.None, ShowPointer: false));
        if (close != null) hits.Add(close);
    }

    private void DrawManaDetails()
    {
        if (!CurrentManaReading || ManaReadingPlayer is not { } player) return;
        const float size = .65f;
        var parts = PublicResources.Mana(player, includeEmpty: true).Select(part => {
            int glyph = part.Symbol.Length > 0 ? 28 : 0;
            int label = glyph + canvas.TextWidth(part.Name + " ", size, true);
            int amount = canvas.TextWidth(part.Amount.ToString(), size, true);
            return (Part: part, Glyph: glyph, Label: label, Amount: amount, Width: label + amount + 12);
        }).ToArray();
        int ColumnWidth(int column) => parts.Where((_, index) => index % 2 == column).Select(part => part.Width).DefaultIfEmpty(0).Max();
        int closeWidth = Math.Max(84, canvas.TextWidth("Close", .76f) + 24);
        int width = Math.Max(ColumnWidth(0) + ColumnWidth(1) + 10, canvas.TextWidth("Unspent mana", .7f, true) + closeWidth + 16);
        int rowHeight = Math.Max(28, parts.Max(part => canvas.TextSize(part.Part.Name, size, part.Label, 1, true).Y) + 6);
        int header = Math.Max(34, canvas.TextSize("Unspent mana", .7f, width, 1, true).Y + 8);
        int height = header + (parts.Length + 1) / 2 * rowHeight;
        var hero = SeatHero(player.Id);
        var anchor = new Rectangle((int)hero.X - 58, (int)hero.Y - 58, 116, 116);
        var occupied = matchPresentation.Cards.Cards.Select(card => card.Display.Bounds).ToList();
        if (!battlefieldReaderStateBounds.IsEmpty) occupied.Add(battlefieldReaderStateBounds);
        occupied.AddRange(manaSourcePaint.SelectMany(source => new[] { source.Bounds, source.OutputBounds }));
        if (manaJourneyPaint is { } bill) { occupied.Add(bill.Bounds); occupied.Add(bill.SourceBounds); }
        foreach (var seat in match!.Players) {
            var portrait = SeatHero(seat.Id);
            occupied.Add(new((int)portrait.X - 58, (int)portrait.Y - 58, 116, 116));
            occupied.AddRange(TurnControlLayout.For(portrait).Reservations);
        }
        var area = DecisionActionArea(anchor, width, height, reserveBattlefield: true,
            additionalReservations: occupied, minimumWidth: width, boundedWorkspace: true);
        canvas.Text("Unspent mana", area.X + 4, area.Y + 3, Ink, .7f, width - closeWidth - 12, 1, true);
        Button("close-overlay", "Close", area.X + width - closeWidth, area.Y, closeWidth, new MatchIntent.CloseView(), height: header - 6);
        var painted = new List<PublicResourcePartPaint>();
        for (int index = 0; index < parts.Length; index++) {
            var item = parts[index];
            int x = area.X + (index % 2 == 0 ? 0 : ColumnWidth(0) + 10), y = area.Y + header + index / 2 * rowHeight;
            var bounds = new Rectangle(x, y, item.Width, rowHeight - 3);
            canvas.Gradient(bounds, new Color(8, 17, 23) * .94f, new Color(8, 17, 23) * .6f);
            if (item.Part.Symbol.Length > 0) PaintManaSymbol(item.Part.Symbol, new(x + 13, bounds.Center.Y), 22);
            canvas.Text(item.Part.Name, x + item.Glyph + 4, y + 1, Ink, size, item.Label - item.Glyph, 1, true);
            canvas.Text(item.Part.Amount.ToString(), x + item.Label + 4, y + 1, Ink, size, item.Amount, 1, true);
            painted.Add(new(item.Part.Key, item.Part.Name, item.Part.Amount.ToString(), bounds, item.Amount, item.Amount));
        }
        manaPoolDetail = new("Unspent mana", PublicResources.ManaTotal(player), new(area.X, area.Y, width, height), painted.ToArray(), 0, 0);
    }
}
