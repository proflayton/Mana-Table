using System.Collections.Immutable;
using Mana.Planning;
using Mana.Contracts;

namespace Mana.AI;

public enum AiManaPlanStatus { Funded, Shortfall, Unknown, BudgetExceeded }
public abstract record AiManaStep
{
    private protected AiManaStep() { }
    public sealed record Pool(string Color, int Amount = 1) : AiManaStep;
    public sealed record Source(string SourceCardId, string SourceCardKey, string ActionId,
        ImmutableDictionary<string, int> Output) : AiManaStep;
}

// A witness for nominal mana assignment, never permission to activate a source
// or proof of a modified cost, replacement, restriction or eventual resolution.
public sealed record AiManaPlan(AiManaPlanStatus Status, ImmutableArray<AiManaStep> Steps,
    int WorkSpent, string ReasonCode);

// An assessment of read-only resources deliberately exposes no action steps.
public sealed record AiManaFundingAssessment(AiManaPlanStatus Status, int WorkSpent, string ReasonCode);

/// <summary>
/// Maps current seat facts into the shared neutral planner and binds resource
/// positions back to this observation's offered actions. Assignment and resource
/// state belong to Mana.Planning; executable action authority stays here.
/// </summary>
public static class AiManaPlanner
{
    private static readonly string[] Colors = ["W", "U", "B", "R", "G", "C"];
    public static AiManaPlan ForAction(AiDecisionRequest request, ActionSemantics action,
        int extraGeneric = 0, CancellationToken cancellation = default, AiResourceReservation? preserve = null)
    {
        var costs = action.Costs.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (costs.Any(cost => cost.Amount == null)) return new(AiManaPlanStatus.Unknown, [], 0, "mana-unknown-cost-quantity");
        long generic = costs.Sum(cost => Math.Max(0L, cost.Amount ?? 0)) + extraGeneric;
        if (generic is < 0 or > 1024) return new(AiManaPlanStatus.Unknown, [], 0, "mana-bill-bound");
        string? reserved = action.Costs.Any(ConsumesInitiatingSource)
            ? request.Frame.SourceActions.FirstOrDefault(set => set.Actions.Any(offer => offer.Id == action.ActionId))?.SourceCardId : null;
        return Plan(request.Frame, costs.SelectMany(cost => cost.ManaSymbols),
            (int)generic, request.Work, cancellation, reserved, preserve: preserve);
    }

    public static AiManaPlan ForPayment(AiDecisionRequest request, CancellationToken cancellation = default,
        AiResourceReservation? preserve = null)
        => Plan(request.Frame, request.Frame.Semantics.UnpaidMana, 0, request.Work, cancellation, preserve: preserve);

    internal static AiManaFundingAssessment ForCostSelection(AiDecisionRequest request,
        CostPaymentFunding payment, IReadOnlySet<string> unavailable)
    {
        if (payment.PayerId != request.Frame.State.ViewerId || payment.ManaCostKnowledge == SemanticKnowledge.Unsupported)
            return new(AiManaPlanStatus.Unknown, 0, "cost-payment-scope-or-bill-unsupported");
        var mana = payment.RemainingCosts.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (mana.Any(cost => cost.Amount == null || cost.ManaSymbols.Any(symbol => symbol.Trim('{', '}') == "X")))
            return new(AiManaPlanStatus.Unknown, 0, "cost-payment-remaining-bill-unknown");
        long generic = mana.Sum(cost => Math.Max(0L, cost.Amount ?? 0));
        if (generic > 1024) return new(AiManaPlanStatus.Unknown, 0, "mana-bill-bound");
        var funding = payment.Funding with {
            Sources = payment.Funding.Sources.Where(source => !unavailable.Contains(source.SourceCardId)).ToImmutableArray()
        };
        var plan = Plan(null, mana.SelectMany(cost => cost.ManaSymbols), (int)generic,
            request.Work, default, funding: funding);
        return new(plan.Status, plan.WorkSpent, plan.ReasonCode);
    }

    // One joint assignment, rather than two independently affordable bills.
    // This remains nominal: actual engine payment can change the bill later.
    public static AiManaPlan WithReservation(AiDecisionRequest request, ActionSemantics action,
        AiResourceReservation reservation, int extraGeneric = 0)
    {
        var costs = action.Costs.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (costs.Any(cost => cost.Amount == null)) return new(AiManaPlanStatus.Unknown, [], 0, "reservation-cost-unknown");
        long generic = costs.Sum(cost => Math.Max(0L, cost.Amount ?? 0)) + extraGeneric + reservation.GenericMana;
        if (generic is < 0 or > 1024) return new(AiManaPlanStatus.Unknown, [], 0, "reservation-bill-bound");
        string? consumed = action.Costs.Any(ConsumesInitiatingSource)
            ? request.Frame.SourceActions.FirstOrDefault(set => set.Actions.Any(offer => offer.Id == action.ActionId))?.SourceCardId : null;
        if (consumed == reservation.SourceCardId) return new(AiManaPlanStatus.Shortfall, [], 0, "reservation-source-consumed");
        return Plan(request.Frame, costs.SelectMany(cost => cost.ManaSymbols).Concat(reservation.ManaSymbols),
            (int)generic, request.Work, default, consumed);
    }

    public static AiManaFundingAssessment ForActionInvestment(AiDecisionRequest request, ActionSemantics action, int x,
        int extraGeneric = 0, CancellationToken cancellation = default)
    {
        if (action.ManaCostKnowledge == SemanticKnowledge.Unsupported)
            return new(AiManaPlanStatus.Unknown, 0, "investment-cost-unsupported");
        if (!InvestmentBill(action.Costs, x, extraGeneric, out var symbols, out int generic, out string reason))
            return new(AiManaPlanStatus.Unknown, 0, reason);
        string? reserved = action.Costs.Any(ConsumesInitiatingSource)
            ? request.Frame.SourceActions.FirstOrDefault(set => set.Actions.Any(offer => offer.Id == action.ActionId))?.SourceCardId : null;
        if (action.ProspectiveFunding is { Knowledge: SemanticKnowledge.Unsupported })
            return new(AiManaPlanStatus.Unknown, 0, "investment-funding-unsupported");
        var plan = Plan(request.Frame, symbols, generic, request.Work, cancellation, reserved, action.ProspectiveFunding);
        return new(plan.Status, plan.WorkSpent, plan.ReasonCode);
    }

    public static AiManaFundingAssessment ForNumericInvestment(AiDecisionRequest request, int x,
        int reserveGeneric = 0, CancellationToken cancellation = default)
    {
        if (x < 0 || reserveGeneric < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (request.Frame.Semantics.Numeric is not { Purpose: NumericDecisionPurpose.AnnounceX } numeric)
            return new(AiManaPlanStatus.Unknown, 0, "numeric-cost-purpose-unknown");
        if (numeric.ManaCostKnowledge == SemanticKnowledge.Unsupported || numeric.Funding.Knowledge == SemanticKnowledge.Unsupported)
            return new(AiManaPlanStatus.Unknown, 0, "numeric-cost-or-funding-unsupported");
        if (!InvestmentBill(numeric.Costs, x, reserveGeneric, out var symbols, out int generic, out string reason))
            return new(AiManaPlanStatus.Unknown, 0, reason);
        string? reserved = numeric.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1)
            ? numeric.SourceCardId : null;
        var plan = Plan(null, symbols, generic, request.Work, cancellation, reserved, numeric.Funding);
        return new(plan.Status, plan.WorkSpent, plan.ReasonCode);
    }

    private static bool InvestmentBill(ImmutableArray<ActionCost> allCosts, int x, int extraGeneric,
        out ImmutableArray<string> symbols, out int generic, out string reason)
    {
        if (x < 0 || extraGeneric < 0) throw new ArgumentOutOfRangeException(nameof(x));
        symbols = []; generic = 0; reason = "numeric-nonmana-cost-coupling";
        if (allCosts.Count(cost => cost.Kind == ActionCostKind.Tap && cost.Amount > 0) > 1
            || allCosts.Any(cost => cost.Kind != ActionCostKind.Mana
                && !(cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1)
                && !(cost.Kind != ActionCostKind.Other && cost.Amount == 0 && cost.ManaSymbols.IsEmpty)))
            return false;
        var costs = allCosts.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (costs.Any(cost => cost.Amount == null)) { reason = "numeric-cost-quantity-unknown"; return false; }
        long amount = extraGeneric + costs.Sum(cost => (long)cost.Amount!.Value);
        var remaining = ImmutableArray.CreateBuilder<string>();
        foreach (string symbol in costs.SelectMany(cost => cost.ManaSymbols)) {
            if (symbol.Trim('{', '}') == "X") amount += x;
            else remaining.Add(symbol);
        }
        if (amount > 1024) { reason = "mana-bill-bound"; return false; }
        symbols = remaining.ToImmutable(); generic = (int)amount; reason = ""; return true;
    }

    private static bool ConsumesInitiatingSource(ActionCost cost) => cost.FromSource && cost.Amount != 0
        && cost.Kind is ActionCostKind.Tap or ActionCostKind.Sacrifice or ActionCostKind.Discard or ActionCostKind.Exile;

    public static AiManaFundingAssessment EvaluateFunding(ManaFundingContext funding, IEnumerable<string> symbols,
        int generic = 0, int workLimit = 2048, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(funding); ArgumentNullException.ThrowIfNull(symbols);
        if (workLimit <= 0) throw new ArgumentOutOfRangeException(nameof(workLimit));
        if (funding.Knowledge == SemanticKnowledge.Unsupported) return new(AiManaPlanStatus.Unknown, 0, "numeric-funding-unsupported");
        var plan = Plan(null, symbols, generic, new(workLimit), cancellation, funding: funding);
        return new(plan.Status, plan.WorkSpent, plan.ReasonCode);
    }

    // Offline evaluation uses the same bounded search, with no live connection.
    public static AiManaPlan Evaluate(AiDecisionFrame frame, IEnumerable<string> symbols,
        int generic = 0, int workLimit = 2048, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(symbols);
        if (workLimit <= 0) throw new ArgumentOutOfRangeException(nameof(workLimit));
        return Plan(frame, symbols, generic, new(workLimit), cancellation);
    }

    public static AiManaPlan EvaluateAction(AiDecisionFrame frame, ActionSemantics action, int extraGeneric = 0,
        int workLimit = 2048, CancellationToken cancellation = default)
    {
        if (workLimit <= 0) throw new ArgumentOutOfRangeException(nameof(workLimit));
        return ForAction(new(frame, new([], []), 0, TimeSpan.FromSeconds(3), new(workLimit)), action, extraGeneric, cancellation);
    }

    private static AiManaPlan Plan(AiDecisionFrame? frame, IEnumerable<string> symbols, int generic,
        AiWorkBudget budget, CancellationToken cancellation, string? reservedSource = null, ManaFundingContext? funding = null,
        AiResourceReservation? preserve = null)
    {
        var authority = new Dictionary<(string Source, int Route), (SourceActionSet Set, SourceAction Action, ActionSemantics Metadata)>();
        long? poolCapacity = null;
        if (funding == null) {
            ArgumentNullException.ThrowIfNull(frame);
            var pool = Colors.ToImmutableDictionary(color => color, color => {
                int count = (int)Math.Clamp((long)frame.State.Viewer!.Mana.GetValueOrDefault(color)
                    - (preserve?.PoolMana.GetValueOrDefault(color) ?? 0), 0, 1024);
                return frame.Task == AiTask.Payment ? frame.Domain.PoolManaColors.Contains(color) ? Math.Min(1, count) : 0 : count;
            });
            if (frame.Task == AiTask.Payment)
                poolCapacity = Colors.Sum(color => Math.Max(0L, (long)frame.State.Viewer!.Mana.GetValueOrDefault(color)
                    - (preserve?.PoolMana.GetValueOrDefault(color) ?? 0)));
            var sources = ImmutableArray.CreateBuilder<ManaFundingSource>();
            var metadata = frame.Domain.Actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
            foreach (var set in frame.SourceActions) {
                if (preserve?.ManaSourceIds.Contains(set.SourceCardId) == true) continue;
                if (CardViews.FindActionSource(frame.State, set.SourceCardKey) == null) continue;
                var routes = ImmutableArray.CreateBuilder<ManaFundingRoute>();
                foreach (var action in set.Actions.OrderBy(action => action.Id, StringComparer.Ordinal)) {
                    if (!metadata.TryGetValue(action.Id, out var facts) || !facts.ManaAbility
                        || facts.Affordability == ActionAffordability.Unavailable || facts.PaymentManaColors is { IsEmpty: true }) continue;
                    var production = facts.Production;
                    if (frame.Task == AiTask.Payment && facts.PaymentManaColors is { } usable)
                        production = production with { Alternatives = production.Alternatives.Select(output => output with {
                            Colors = output.Colors.ToImmutableDictionary(pair => pair.Key, pair => usable.Contains(pair.Key) ? pair.Value : 0)
                        }).ToImmutableArray() };
                    authority.Add((set.SourceCardId, routes.Count), (set, action, facts));
                    routes.Add(new(facts.Costs, production, SemanticKnowledge.Known));
                }
                if (routes.Count > 0) sources.Add(new(set.SourceCardId, routes.ToImmutable()));
            }
            funding = new(SemanticKnowledge.Known, pool, sources.ToImmutable());
        } else {
            // Numeric/prospective facts deliberately model only finite one-use
            // resources. Live offered routes retain their existing bounded
            // single-use approximation for free or reversible producers.
            funding = funding with { Sources = funding.Sources.Select(source => source with {
                Routes = source.Routes.Select(route => ManaFundingPlanner.IsFiniteOneUse(route.Costs)
                    ? route : route with { Eligibility = SemanticKnowledge.Unknown }).ToImmutableArray()
            }).ToImmutableArray() };
        }
        var plan = ManaFundingPlanner.Evaluate(funding, symbols, generic, budget, reservedSource, cancellation, poolCapacity);
        var steps = ImmutableArray.CreateBuilder<AiManaStep>();
        foreach (var step in plan.Steps) {
            if (step is ManaFundingStep.Pool pool) steps.Add(new AiManaStep.Pool(pool.Color, pool.Amount));
            else if (step is ManaFundingStep.Source source && authority.TryGetValue((source.SourceCardId, source.RouteIndex), out var current))
                steps.Add(new AiManaStep.Source(source.SourceCardId, current.Set.SourceCardKey, current.Action.Id,
                    current.Metadata.Production.Alternatives[source.AlternativeIndex].Colors));
        }
        return new(plan.Status switch {
            ManaFundingStatus.Funded => AiManaPlanStatus.Funded, ManaFundingStatus.Shortfall => AiManaPlanStatus.Shortfall,
            ManaFundingStatus.Unknown => AiManaPlanStatus.Unknown, _ => AiManaPlanStatus.BudgetExceeded
        }, steps.ToImmutable(), plan.WorkSpent, plan.ReasonCode);
    }
}
