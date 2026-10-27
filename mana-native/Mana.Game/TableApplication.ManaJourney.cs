using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Planning;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record ManaSourcePaint(string SourceId, Rectangle Bounds,
        ImmutableDictionary<string, int> Output, bool Suggested);
    internal sealed record ManaJourneyPaint(string SourceId, bool Payment, Rectangle Bounds,
        ImmutableArray<ManaBillPart> Parts, string Advice, ManaFundingStatus? FundingStatus, string SourceZone, Rectangle SourceBounds)
    {
        public ImmutableArray<string> Symbols => Parts.Select(part => part.Symbol).ToImmutableArray();
    }
    private sealed record ManaJourneyRead(Card Source, bool Payment, ImmutableArray<ManaBillPart> Parts,
        ManaFundingContext? Funding, ManaFundingPlan? Plan, string Advice);
    private ManaJourneyRead? manaJourney;
    private ManaJourneyPaint? manaJourneyPaint;
    private readonly List<ManaSourcePaint> manaSourcePaint = [];
    private string manaPlanStamp = "";
    private ManaFundingPlan? manaPlan;
    private string manaHoverIdentity = "";
    private double manaHoverSince;
    private bool ManaPaymentSurface => manaJourneyPaint is { Payment: true };

    // Called once per composed frame. Planning is bounded and memoized by the
    // exact source-query generation or current payment snapshot, never by name.
    private void PrepareManaJourney()
    {
        manaJourney = null; manaJourneyPaint = null; manaSourcePaint.Clear();
        if (match is not { Status: "playing", Viewer: not null, Decision: { } decision } state
            || session.NeedsRecovery || OverlayOpen && !BattlefieldFocused || CardCollectionOpen) return;
        if (decision.Payload is PaymentPayload payment && (!payment.UnpaidManaParts.IsDefaultOrEmpty || !payment.UnpaidMana.IsDefaultOrEmpty)
            && decision.Context.Source?.Card is { FaceDown: false } paying) {
            var parts = !payment.UnpaidManaParts.IsDefaultOrEmpty ? payment.UnpaidManaParts
                : CountSymbols(ManaCostPresentation.Unpaid(payment.UnpaidMana));
            var plan = PlanMana("payment:" + state.Id + ":" + state.ViewerId + ":" + decision.Id + ":" + state.Revision,
                payment.FundingContext, parts, null);
            manaJourney = new(paying, true, parts, payment.FundingContext, plan,
                plan?.Status != ManaFundingStatus.Funded ? "Choose mana sources"
                    : plan.Steps.OfType<ManaFundingStep.Source>().Any() ? "Choose the lit sources" : "Use floating mana");
            return;
        }
        if (decision.Payload is not PriorityPayload || ReaderActionSource() is not { } source) return;
        var ready = ReaderActionsMatch(source);
        var options = ready ? sourceActions.View.Actions.Where(action => action.CostPreview != null).ToArray() : [];
        var pointedAction = pointerSurface?.Intent as MatchIntent.UseSourceAction;
        var offered = pointedAction == null ? options.Length == 1 ? options[0] : null
            : options.FirstOrDefault(action => action.Id == pointedAction.Id);
        if (offered?.CostPreview is { } preview) {
            var costs = preview.Costs;
            bool inCommand = state.Viewer.Zone("Command").Cards.Any(card => CardViews.Same(card, source));
            if (inCommand && offered.Kind == "cast" && CardPresentation.CommanderTax(source) is > 0 and var tax)
                costs = costs.Add(new(ActionCostKind.Mana, tax, false, ""));
            var symbols = ManaCostPresentation.Nominal(costs);
            if (symbols.IsEmpty) return;
            var parts = CountSymbols(symbols);
            var plan = ManaCostPresentation.CanPlanNominal(preview) ? PlanMana("preview:" + state.Id + ":" + state.Revision + ":" + sourceActions.View.Generation + ":" + offered.Id,
                preview.Funding, parts, ManaCostPresentation.ReservedSource(preview.Costs, CardViews.Identity(source))) : null;
            string advice = symbols.Contains("{X}") ? "Choose X when casting"
                : plan?.Status == ManaFundingStatus.Funded ? "Suggested taps · estimate"
                : "Check sources manually";
            manaJourney = new(source, false, parts, preview.Funding, plan, advice);
        } else if (source.ManaCost.Length > 0 && source.ManaCost != "no cost") {
            var symbols = ManaCostPresentation.Printed(source.ManaCost);
            if (symbols.IsEmpty) return;
            manaJourney = new(source, false, CountSymbols(symbols), null, null,
                options.Length > 1 ? "Choose a casting mode" : ready ? "Read the current action" : "Reading sources…");
        }
    }

    private static ImmutableArray<ManaBillPart> CountSymbols(ImmutableArray<string> symbols) =>
        symbols.GroupBy(symbol => symbol).Select(group => new ManaBillPart(group.Key, group.Count())).ToImmutableArray();

    private ManaFundingPlan? PlanMana(string stamp, ManaFundingContext? funding,
        ImmutableArray<ManaBillPart> parts, string? reserved)
    {
        if (manaPlanStamp == stamp) return manaPlan;
        manaPlanStamp = stamp;
        // A UI preview has its own small allowance; it never borrows or resets
        // an automated seat's work/causal budget and never emits a command.
        return manaPlan = funding == null ? null : ManaFundingPlanner.EvaluateBill(funding, parts,
            workLimit: 512, reservedSource: reserved);
    }

    private Card? HoverManaSource()
    {
        if (match?.Decision?.Payload is not PriorityPayload || dragging || matchPresentation.Cards.Pinned != null
            || pointerSurface is not { Zone: "Hand" or "Command", Card: { FaceDown: false } card } hit
            || !CurrentHit(hit) || CardContext.CurrentOccurrence(hit.Zone!, card) is not { Key.Length: > 0 } current) {
            manaHoverIdentity = ""; return null;
        }
        string identity = CardViews.Identity(current);
        if (identity != manaHoverIdentity) { manaHoverIdentity = identity; manaHoverSince = now; }
        return now - manaHoverSince >= .16 ? current : null;
    }

    private void DrawManaJourney()
    {
        if (manaJourney is not { } journey || match?.Viewer is not { } viewer) return;
        var payer = journey.Payment && match.Decision?.Payload is PaymentPayload paymentOwner
            ? match.Players.FirstOrDefault(player => player.Id == (paymentOwner.PayerId ?? match.ViewerId)) ?? viewer : viewer;
        DrawManaSourceWitness(journey, payer);
        string identity = CardViews.Identity(journey.Source);
        var matching = matchPresentation.Cards.Cards.Where(card => CardViews.Same(card.Card, journey.Source)).ToArray();
        var entry = journey.Payment ? matching.LastOrDefault(card => card.Id == "payment:" + match.Decision!.Id)
                ?? matching.LastOrDefault(card => card.Zone == "Stack") ?? matching.LastOrDefault()
            : matching.LastOrDefault();
        if (entry == null || entry.Display.Bounds.IsEmpty) return;
        var face = entry.Display.Bounds;
        // Counted requirements are drawn directly. X=129 or a million repeated
        // shards must not allocate or paint one object for every mana unit.
        var tokens = journey.Parts.Where(part => part.Count > 0).ToArray();
        const int symbolSide = 36, advance = 44;
        int symbolWidth = tokens.Sum(token => advance + (token.Count > 1 ? canvas.TextWidth("×" + token.Count, .66f, true) : 0));
        int width = Math.Clamp(Math.Max(symbolWidth + 20, canvas.TextWidth(journey.Advice, .66f) + 20), 220, 360);
        var positions = new List<(ManaBillPart Part, int X, int Row)>();
        int columnX = 12, row = 0;
        foreach (var token in tokens) {
            int amountWidth = token.Count > 1 ? canvas.TextWidth("×" + token.Count, .66f, true) : 0;
            if (columnX + advance + amountWidth > width - 8 && columnX > 12) { columnX = 12; row++; }
            positions.Add((token, columnX, row)); columnX += advance + amountWidth;
        }
        int rows = row + 1;
        int height = 31 + rows * 42 + (journey.Payment ? 54 : 25);
        // Optional metadata must not turn into an unbounded billboard or a
        // negative viewport clamp. Exceptionally complex bills keep the normal
        // exact engine instructions and current manual payment controls.
        if (rows > 4) return;
        var occupied = manaSourcePaint.Select(source => source.Bounds).ToList();
        foreach (var resource in journey.Funding?.Sources ?? [])
            if (matchPresentation.Cards.TryGetPose(resource.SourceCardId, out var resourcePose)) occupied.Add(resourcePose.Bounds);
        if (!sourceReaderActionsBounds.IsEmpty) occupied.Add(sourceReaderActionsBounds);
        if (matchPresentation.Cards.Pinned is { } pinned && !CardViews.Same(pinned.Card, journey.Source)) occupied.Add(pinned.Display.Bounds);
        var room = DecisionActionArea(face, width, height, additionalReservations: occupied);
        if (room.Width < width || room.Height < height) return;
        int x = room.X;
        int y = Math.Clamp(entry.Display.Size.X >= 300 ? face.Top + 12 : face.Top - height - 10, room.Top, room.Bottom - height);
        var area = new Rectangle(x, y, width, height);
        var accent = journey.Plan?.Status == ManaFundingStatus.Funded ? new Color(135, 221, 194) : Gold;
        // The face remains the object; this small caption sits outside its art
        // and rules instead of opening a second inspector or resource panel.
        canvas.Gradient(area, new Color(8, 17, 23) * .96f, new Color(8, 17, 23) * .78f);
        canvas.Line(new(x + 8, y + 23), new(x + width - 8, y + 23), accent * .7f, 1);
        canvas.Text(journey.Payment ? "PAY REMAINING" : "MANA COST", x + 9, y + 3, accent, .62f, width - 18, 1, true);
        foreach (var token in positions) {
            int sx = x + token.X, sy = y + 48 + token.Row * 42;
            PaintCostSymbol(token.Part.Symbol, new(sx + symbolSide / 2, sy), symbolSide);
            if (token.Part.Count > 1) canvas.Text("×" + token.Part.Count, sx + symbolSide + 2, sy - 12, Ink, .66f,
                canvas.TextWidth("×" + token.Part.Count, .66f, true) + 3, 1, true);
        }
        int adviceY = y + 29 + rows * 42;
        canvas.Text(journey.Advice, x + 9, adviceY, Muted, .66f, width - 18, 1);
        if (journey.Payment && match.Decision?.Payload is PaymentPayload payment) {
            int controlY = area.Bottom - 31;
            if (payment.Pay.Enabled) Button("mana-auto", "Auto pay", x + 8, controlY, 100,
                new MatchIntent.ConfirmDecision(), !busy, height: 27);
            if (payment.Cancel.Enabled) Button("mana-cancel", payment.Cancel.Label, x + width - 94, controlY, 86,
                new MatchIntent.CancelDecision(), !busy, height: 27);
        }
        manaJourneyPaint = new(identity, journey.Payment, area, journey.Parts, journey.Advice, journey.Plan?.Status, entry.Zone, face);
    }

    private void DrawManaSourceWitness(ManaJourneyRead journey, Player viewer)
    {
        var suggested = journey.Plan?.Status == ManaFundingStatus.Funded
            ? journey.Plan.Steps.OfType<ManaFundingStep.Source>().ToArray() : [];
        var own = viewer.Zone("Battlefield").Cards;
        var sources = suggested.Select(step => (step.SourceCardId, step.Output, Suggested: true)).ToList();
        if (journey.Payment && journey.Plan?.Status != ManaFundingStatus.Funded && journey.Funding != null)
            sources.AddRange(journey.Funding.Sources.Where(source => own.Any(card => CardViews.Identity(card) == source.SourceCardId && card.Selectable && !card.Tapped))
                .Select(source => (source.SourceCardId, ImmutableDictionary<string, int>.Empty, Suggested: false)));
        var seat = tableScene!.Seats.FirstOrDefault(seat => seat.PlayerId == viewer.Id);
        foreach (var group in sources.Where(source => own.Any(card => CardViews.Identity(card) == source.SourceCardId && !card.FaceDown && !card.Tapped))
            .GroupBy(source => seat?.Cohort(source.SourceCardId)?.Front.Id ?? source.SourceCardId)) {
            if (!matchPresentation.Cards.TryGetPose(group.Key, out var pose)) continue;
            var output = group.SelectMany(source => source.Output).GroupBy(part => part.Key)
                .ToImmutableDictionary(parts => parts.Key, parts => parts.Sum(part => part.Value));
            bool confident = group.All(source => source.Suggested);
            var accent = confident ? new Color(135, 221, 194) : Gold;
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(9) }, accent * .92f, 2);
            var point = pose.Point(.5f, -.1f);
            int offset = 0;
            foreach (var part in output.OrderBy(part => part.Key, StringComparer.Ordinal)) {
                PaintManaSymbol(part.Key, point + new Vector2(offset, 0), 26);
                if (part.Value > 1) canvas.Text(part.Value.ToString(), (int)point.X + offset + 16, (int)point.Y - 12, Ink, .64f, 34, 1, true);
                offset += part.Value > 1 ? 52 : 31;
            }
            foreach (var source in group) manaSourcePaint.Add(new(source.SourceCardId, pose.Bounds, source.Output, confident));
        }
    }

    private void PaintCostSymbol(string symbol, Vector2 point, int size)
    {
        string value = symbol.Trim('{', '}');
        var crop = SourceActionTextLayout.SymbolCrop(value);
        if (!crop.IsEmpty && resources.ManaSymbols is { } image && crop.Right <= image.Width && crop.Bottom <= image.Height)
            canvas.Sprite(image, point, new(size), 0, Color.White, crop);
        else {
            canvas.Circle(point, size / 2f, new(211, 207, 191));
            canvas.CenterText(value, new((int)point.X - size / 2, (int)point.Y - size / 2, size, size), new(22, 27, 31), .53f, true);
        }
    }
}
