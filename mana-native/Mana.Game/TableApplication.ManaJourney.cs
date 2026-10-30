using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Planning;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record ManaSourcePaint(string SourceId, Rectangle Bounds,
        ImmutableDictionary<string, int> Output, bool Suggested)
    {
        public Rectangle OutputBounds { get; init; }
    }
    internal sealed record ManaJourneyPaint(string SourceId, bool Payment, Rectangle Bounds,
        ImmutableArray<ManaBillPart> Parts, string Advice, ManaFundingStatus? FundingStatus, string SourceZone, Rectangle SourceBounds)
    {
        public ImmutableArray<string> Symbols => Parts.Select(part => part.Symbol).ToImmutableArray();
    }
    private sealed record ManaJourneyRead(Card Source, bool Payment, ImmutableArray<ManaBillPart> Parts,
        ManaFundingContext? Funding, ManaFundingPlan? Plan, string Advice)
    {
        public string Caption { get; init; } = "MANA COST";
    }
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
            || session.NeedsRecovery || OverlayOpen && !BattlefieldFocused && !CurrentManaReading || CardCollectionOpen) return;
        if (QuietUnpayableOptionalPayment) return;
        if (decision.Payload is OptionsPayload { ManaPaymentContext: { Source.Card: { FaceDown: false } owningCostSource } context }
            && !context.UnpaidManaParts.IsDefaultOrEmpty) {
            // The color callback owns only its offered choice. Its parent bill
            // stays readable, without reviving parent payment authority.
            manaJourney = new(owningCostSource, true, context.UnpaidManaParts, null, null, "Choose the source's mana color");
            return;
        }
        if (decision.Payload is EntitySelectionPayload { ManaReduction: { } reduction }
            && decision.Context.Source?.Card is { FaceDown: false } reductionSource) {
            manaJourney = new(reductionSource, true, reduction.RemainingManaParts, null, null,
                reduction.RemainingManaParts.IsEmpty ? "" : "Choose highlighted cards");
            return;
        }
        if (decision.Payload is PaymentPayload payment && (!payment.UnpaidManaParts.IsDefaultOrEmpty || !payment.UnpaidMana.IsDefaultOrEmpty)
            && decision.Context.Source?.Card is { FaceDown: false } paying) {
            var parts = !payment.UnpaidManaParts.IsDefaultOrEmpty ? payment.UnpaidManaParts
                : CountSymbols(ManaCostPresentation.Unpaid(payment.UnpaidMana));
            var plan = PlanMana("payment:" + state.Id + ":" + state.ViewerId + ":" + decision.Id + ":" + state.Revision,
                payment.FundingContext, parts, null);
            manaJourney = new(paying, true, parts, payment.FundingContext, plan,
                payment.OptionalPaymentSupport?.Availability == OptionalManaPaymentAvailability.Unpayable ? "Cannot pay this optional cost"
                    : plan?.Status != ManaFundingStatus.Funded ? "Choose mana sources"
                    : plan.Steps.OfType<ManaFundingStep.Source>().Any() ? "Choose the lit sources" : "Use floating mana");
            return;
        }
        if (decision.Payload is not PriorityPayload || ReaderActionSource() is not { } source) return;
        var ready = ReaderActionsMatch(source);
        var options = ready ? sourceActions.View.Actions.Where(action => action.CostPreview != null).ToArray() : [];
        var pointedAction = pointerSurface?.Intent as MatchIntent.UseSourceAction;
        // An offer without cost metadata cannot borrow the source face's printed
        // casting cost. In particular, a land face does not cost its spell face's mana.
        if (pointedAction != null && (!ready || sourceActions.View.Actions
            .FirstOrDefault(action => action.Id == pointedAction.Id)?.CostPreview == null)) return;
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
                : plan?.Status == ManaFundingStatus.Funded ? "Suggested mana · estimate"
                : "Check sources manually";
            manaJourney = new(source, false, parts, preview.Funding, plan, advice) {
                Caption = offered.Kind == "cast" ? "SPELL COST" : "MANA COST"
            };
        } else if (source.ManaCost.Length > 0 && source.ManaCost != "no cost") {
            var symbols = ManaCostPresentation.Printed(source.ManaCost);
            if (symbols.IsEmpty) return;
            manaJourney = new(source, false, CountSymbols(symbols), null, null,
                options.Length > 1 ? "Choose a casting mode" : ready ? "Choose an action" : "Reading sources…") {
                Caption = "PRINTED SPELL COST"
            };
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
        int payerId = match.Decision?.Payload switch {
            PaymentPayload paymentOwner => paymentOwner.PayerId ?? match.ViewerId,
            OptionsPayload { ManaPaymentContext: { } colorOwner } => colorOwner.PayerId,
            _ => match.ViewerId
        };
        var payer = journey.Payment ? match.Players.FirstOrDefault(player => player.Id == payerId) ?? viewer : viewer;
        DrawManaSourceWitness(journey, payer);
        DrawManaPoolWitness(journey, viewer);
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
        var declineBatch = !ResolvingCapturedStack ? RepeatedDeclineOffer : null;
        string declineLabel = declineBatch == null ? "" : "Decline " + declineBatch.DeclineProgress!.CapturedCount + " + resolve queue";
        var currentPayment = journey.Payment ? match.Decision?.Payload as PaymentPayload : null;
        bool sourcePolicy = currentPayment != null && CurrentOptionalPayment != null;
        string payLabel = sourcePolicy ? "Pay once" : "Auto pay";
        string declineOnceLabel = sourcePolicy ? "Decline once" : currentPayment?.Cancel.Label ?? "";
        int payWidth = canvas.TextWidth(payLabel, .76f) + 24;
        int declineWidth = currentPayment?.Cancel.Enabled == true ? canvas.TextWidth(declineOnceLabel, .76f) + 24 : 0;
        const int symbolSide = 36, advance = 44;
        int symbolWidth = tokens.Sum(token => advance + (token.Count > 1 ? canvas.TextWidth("×" + token.Count, .66f, true) : 0));
        int width = Math.Clamp(Math.Max(symbolWidth + 20, canvas.TextWidth(journey.Advice, .66f) + 20), 220, 360);
        if (currentPayment != null) width = Math.Max(width, Math.Min(420, payWidth + declineWidth + 28));
        if (declineLabel.Length > 0) width = Math.Max(width, Math.Min(420, canvas.TextWidth(declineLabel, .76f, true) + 40));
        if (sourcePolicy) width = Math.Max(width, 360);
        var positions = new List<(ManaBillPart Part, int X, int Row)>();
        int columnX = 12, row = 0;
        foreach (var token in tokens) {
            int amountWidth = token.Count > 1 ? canvas.TextWidth("×" + token.Count, .66f, true) : 0;
            if (columnX + advance + amountWidth > width - 8 && columnX > 12) { columnX = 12; row++; }
            positions.Add((token, columnX, row)); columnX += advance + amountWidth;
        }
        int rows = tokens.Length == 0 ? 0 : row + 1;
        var reductionChoice = match.Decision?.Payload as EntitySelectionPayload;
        bool reducing = reductionChoice?.ManaReduction != null;
        bool paymentControls = match.Decision?.Payload is PaymentPayload || reducing;
        int batchHeight = declineLabel.Length == 0 ? 0 : Math.Max(40, canvas.TextSize(declineLabel, .76f, width - 40, 80, true).Y + 12);
        int policyHeight = sourcePolicy ? OptionalPaymentControlsHeight(width - 16) : 0;
        int height = 31 + rows * 42 + (reducing ? 68 : paymentControls && journey.Payment ? 54 : 25)
            + (declineBatch != null ? batchHeight + 11 : 0) + policyHeight;
        // Optional metadata must not turn into an unbounded billboard or a
        // negative viewport clamp. Exceptionally complex bills keep the normal
        // exact engine instructions and current manual payment controls.
        if (rows > 4) return;
        var occupied = manaSourcePaint.SelectMany(source => new[] { source.Bounds, source.OutputBounds })
            .Where(bounds => !bounds.IsEmpty).ToList();
        foreach (var resource in journey.Funding?.Sources ?? [])
            if (matchPresentation.Cards.TryGetPose(resource.SourceCardId, out var resourcePose)) occupied.Add(resourcePose.Bounds);
        if (!sourceReaderActionsBounds.IsEmpty) occupied.Add(sourceReaderActionsBounds);
        occupied.AddRange(hits.Where(hit => hit.Id == "read-card-rules").Select(hit => ReaderControlPaintBounds(hit.Bounds)));
        if (matchPresentation.Cards.Pinned is { } pinned && !CardViews.Same(pinned.Card, journey.Source)) occupied.Add(pinned.Display.Bounds);
        // During a color callback the producer owns a separate nearby gesture.
        // Keep the original bill beside its own card, away from that producer.
        // A preview also reserves the physical table when funding is unknown.
        bool sourceWorkspace = match.Decision?.Payload is OptionsPayload { ManaPaymentContext: not null } || reducing;
        var room = DecisionActionArea(face, width, height, reserveBattlefield: sourceWorkspace || !journey.Payment,
            additionalReservations: occupied, minimumWidth: journey.Payment ? 320 : width);
        if (room.Width < width || room.Height < height) return;
        int x = room.X;
        int y = Math.Clamp(entry.Display.Size.X >= 300 ? face.Top + 12 : face.Top - height - 10, room.Top, room.Bottom - height);
        var area = new Rectangle(x, y, width, height);
        var accent = journey.Plan?.Status == ManaFundingStatus.Funded ? new Color(135, 221, 194) : Gold;
        // The face remains the object; this small caption sits outside its art
        // and rules instead of opening a second inspector or resource panel.
        canvas.Gradient(area, new Color(8, 17, 23) * .96f, new Color(8, 17, 23) * .78f);
        canvas.Line(new(x + 8, y + 23), new(x + width - 8, y + 23), accent * .7f, 1);
        canvas.Text(reducing && tokens.Length == 0 ? "COST COVERED" : journey.Payment ? "PAY REMAINING" : journey.Caption,
            x + 9, y + 3, accent, .62f, width - 18, 1, true);
        foreach (var token in positions) {
            int sx = x + token.X, sy = y + 48 + token.Row * 42;
            PaintCostSymbol(token.Part.Symbol, new(sx + symbolSide / 2, sy), symbolSide);
            if (token.Part.Count > 1) canvas.Text("×" + token.Part.Count, sx + symbolSide + 2, sy - 12, Ink, .66f,
                canvas.TextWidth("×" + token.Part.Count, .66f, true) + 3, 1, true);
        }
        int adviceY = y + 29 + rows * 42;
        canvas.Text(journey.Advice, x + 9, adviceY, Muted, .66f, width - 18, 1);
        manaJourneyPaint = new(identity, journey.Payment, area, journey.Parts, journey.Advice, journey.Plan?.Status, entry.Zone, face);
        if (CurrentManaReading) return;
        if (journey.Payment && match.Decision?.Payload is PaymentPayload payment) {
            int controlY = area.Bottom - 31 - (declineBatch != null ? batchHeight + 11 : 0) - policyHeight;
            if (payment.Pay.Enabled && CurrentOptionalPayment?.Availability != OptionalManaPaymentAvailability.Unpayable)
                Button("mana-auto", payLabel, x + 8, controlY, payWidth,
                new MatchIntent.ConfirmDecision(), !busy, height: 27);
            if (payment.Cancel.Enabled) Button("mana-cancel", declineOnceLabel, x + width - declineWidth - 8, controlY, declineWidth,
                new MatchIntent.CancelDecision(), !busy, height: 27);
        }
        if (journey.Payment && declineBatch != null)
            CompleteActionButton("finite-resolution-decline", declineLabel,
                new(x + 8, area.Bottom - batchHeight - 7 - policyHeight, width - 16, batchHeight), new MatchIntent.DeclineRepeatedPayments(), !busy);
        if (sourcePolicy) DrawOptionalPaymentControls(new(x + 8, area.Bottom - policyHeight, width - 16, policyHeight));
        if (reducing && reductionChoice is { } selection) {
            int completeWidth = selection.Cancel.Enabled ? width - 110 : width - 16;
            Button("confirm", selection.Complete.Label, x + 8, area.Bottom - 48, completeWidth,
                new MatchIntent.ConfirmDecision(), selection.Complete.Enabled && !busy,
                accent: true, height: 40, prominent: tokens.Length == 0 && selection.Complete.Enabled);
            if (selection.Cancel.Enabled)
                Button("cancel", selection.Cancel.Label, x + width - 94, area.Bottom - 41, 86,
                    new MatchIntent.CancelDecision(), !busy, height: 27);
        }
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
            var tokens = output.OrderBy(part => part.Key, StringComparer.Ordinal).Select(part => {
                string amount = part.Value > 1 ? part.Value.ToString() : "";
                int width = amount.Length == 0 ? 0 : canvas.TextWidth(amount, .64f, true);
                return (Color: part.Key, Amount: amount, Width: width,
                    Measured: amount.Length == 0 ? Point.Zero : canvas.TextSize(amount, .64f, width, 1, true));
            }).ToArray();
            const int side = 26, quantityGap = 3, tokenGap = 5, margin = 4;
            int stripWidth = tokens.Sum(token => side + (token.Width > 0 ? quantityGap + token.Width : 0))
                + Math.Max(0, tokens.Length - 1) * tokenGap;
            int stripHeight = tokens.Select(token => Math.Max(side, token.Measured.Y + 1)).DefaultIfEmpty(0).Max();
            int left = (int)MathF.Floor(point.X) - side / 2, top = (int)MathF.Floor(point.Y) - side / 2;
            if (tokens.Length > 0 && new Rectangle(0, 0, TableLayout.Width, DesignHeight).Intersects(pose.Bounds)) {
                // Clamp the whole strip while its source is visible, rather
                // than pinning an offscreen source's annotation to an edge.
                left = Math.Clamp(left, margin, TableLayout.Width - stripWidth - margin);
                top = Math.Clamp(top, margin, DesignHeight - stripHeight - margin);
            }
            int offset = 0;
            var outputBounds = Rectangle.Empty;
            foreach (var token in tokens) {
                var center = new Vector2(left + offset + side / 2, top + side / 2);
                PaintManaSymbol(token.Color, center, side);
                var glyph = new Pose(center, new Vector2(side)).Bounds;
                outputBounds = outputBounds.IsEmpty ? glyph : Rectangle.Union(outputBounds, glyph);
                if (token.Width > 0) {
                    int x = left + offset + side + quantityGap, y = top + 1;
                    canvas.Text(token.Amount, x, y, Ink, .64f, token.Width, 1, true);
                    outputBounds = Rectangle.Union(outputBounds, new(x, y, token.Measured.X, token.Measured.Y));
                }
                offset += side + (token.Width > 0 ? quantityGap + token.Width : 0) + tokenGap;
            }
            foreach (var source in group) manaSourcePaint.Add(new(source.SourceCardId, pose.Bounds, source.Output, confident) { OutputBounds = outputBounds });
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
