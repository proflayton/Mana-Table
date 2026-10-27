using System.Collections.Immutable;
using System.Numerics;
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
/// Bounded assignment of one production alternative per physical source. The
/// same solver serves sequencing and current payment; callers supply the exact
/// bill when available. It does not invoke a provider or reconstruct card rules.
/// </summary>
public static class AiManaPlanner
{
    private static readonly string[] Colors = ["W", "U", "B", "R", "G", "C"];
    private sealed record Requirement(int Mask, int GenericAlternative = 0);
    private sealed record Route(AiManaStep.Source? Step, int[] Output);
    private sealed record Source(string Id, ImmutableArray<Route> Routes);

    public static AiManaPlan ForAction(AiDecisionRequest request, ActionSemantics action,
        int extraGeneric = 0, CancellationToken cancellation = default)
    {
        var costs = action.Costs.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (costs.Any(cost => cost.Amount == null)) return new(AiManaPlanStatus.Unknown, [], 0, "mana-unknown-cost-quantity");
        long generic = costs.Sum(cost => Math.Max(0L, cost.Amount ?? 0)) + extraGeneric;
        if (generic is < 0 or > 1024) return new(AiManaPlanStatus.Unknown, [], 0, "mana-bill-bound");
        string? reserved = action.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource)
            ? request.Frame.SourceActions.FirstOrDefault(set => set.Actions.Any(offer => offer.Id == action.ActionId))?.SourceCardId : null;
        return Plan(request.Frame, costs.SelectMany(cost => cost.ManaSymbols),
            (int)generic, request.Work, cancellation, reserved);
    }

    public static AiManaPlan ForPayment(AiDecisionRequest request, CancellationToken cancellation = default)
        => Plan(request.Frame, request.Frame.Semantics.UnpaidMana, 0, request.Work, cancellation);

    public static AiManaFundingAssessment ForNumericInvestment(AiDecisionRequest request, int x,
        int reserveGeneric = 0, CancellationToken cancellation = default)
    {
        if (x < 0 || reserveGeneric < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (request.Frame.Semantics.Numeric is not { Purpose: NumericDecisionPurpose.AnnounceX } numeric)
            return new(AiManaPlanStatus.Unknown, 0, "numeric-cost-purpose-unknown");
        if (numeric.ManaCostKnowledge == SemanticKnowledge.Unsupported || numeric.Funding.Knowledge == SemanticKnowledge.Unsupported)
            return new(AiManaPlanStatus.Unknown, 0, "numeric-cost-or-funding-unsupported");
        if (numeric.Costs.Count(cost => cost.Kind == ActionCostKind.Tap && cost.Amount > 0) > 1
            || numeric.Costs.Any(cost => cost.Kind != ActionCostKind.Mana
                && !(cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1)
                && !(cost.Kind != ActionCostKind.Other && cost.Amount == 0 && cost.ManaSymbols.IsEmpty)))
            return new(AiManaPlanStatus.Unknown, 0, "numeric-nonmana-cost-coupling");
        var costs = numeric.Costs.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (costs.Any(cost => cost.Amount == null)) return new(AiManaPlanStatus.Unknown, 0, "numeric-cost-quantity-unknown");
        long generic = reserveGeneric + costs.Sum(cost => (long)cost.Amount!.Value);
        var symbols = ImmutableArray.CreateBuilder<string>();
        foreach (string symbol in costs.SelectMany(cost => cost.ManaSymbols)) {
            if (symbol.Trim('{', '}') == "X") generic += x;
            else symbols.Add(symbol);
        }
        if (generic > 1024) return new(AiManaPlanStatus.Unknown, 0, "mana-bill-bound");
        string? reserved = numeric.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1)
            ? numeric.SourceCardId : null;
        var plan = Plan(null, symbols, (int)generic, request.Work, cancellation, reserved, numeric.Funding);
        return new(plan.Status, plan.WorkSpent, plan.ReasonCode);
    }

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
        AiWorkBudget budget, CancellationToken cancellation, string? reservedSource = null, ManaFundingContext? funding = null)
    {
        int before = budget.Remaining;
        var required = new List<Requirement>();
        bool uncertain = false, billIncomplete = false, unmodeledReuse = false, billMinimumKnown = true;
        foreach (string symbol in symbols) {
            cancellation.ThrowIfCancellationRequested();
            string value = symbol.Trim('{', '}');
            if (int.TryParse(value, out int amount) && amount >= 0) {
                if ((long)generic + amount > 1024) return Result(AiManaPlanStatus.Unknown, [], "mana-bill-bound");
                generic += amount; continue;
            }
            string[] parts = value.Split('/');
            int mask = 0, alternative = 0;
            foreach (string part in parts) {
                int index = Array.IndexOf(Colors, part);
                if (index >= 0) mask |= 1 << index;
                else if (int.TryParse(part, out int hybridGeneric) && hybridGeneric > 0) alternative = hybridGeneric;
                else { uncertain = true; billMinimumKnown = false; } // X, snow, life and other alternatives remain explicit unknowns.
            }
            if (mask > 0) required.Add(new(mask, alternative));
            else billIncomplete = true;
        }
        if (generic < 0 || generic > 1024 || required.Count > 128)
            return Result(AiManaPlanStatus.Unknown, [], "mana-bill-bound");

        var pool = new int[Colors.Length];
        var sources = new List<Source>();
        bool capacityKnown = true;
        long poolCapacity;
        var physicalCapacity = new Dictionary<string, long>(StringComparer.Ordinal);
        if (funding != null) {
            uncertain |= funding.Knowledge != SemanticKnowledge.Known;
            capacityKnown = funding.Knowledge == SemanticKnowledge.Known;
            for (int color = 0; color < Colors.Length; color++)
                pool[color] = Math.Clamp(funding.Pool.GetValueOrDefault(Colors[color]), 0, 1024);
            poolCapacity = pool.Sum(value => (long)value);
            foreach (var source in funding.Sources.OrderBy(source => source.SourceCardId, StringComparer.Ordinal)) {
                if (!budget.TrySpend()) return Result(AiManaPlanStatus.BudgetExceeded, [], "mana-resource-budget");
                if (source.SourceCardId == reservedSource) continue;
                var routes = ImmutableArray.CreateBuilder<Route>();
                foreach (var route in source.Routes) {
                    if (!budget.TrySpend()) return Result(AiManaPlanStatus.BudgetExceeded, [], "mana-resource-budget");
                    bool pureTap = route.Costs.Count(cost => cost.Kind == ActionCostKind.Tap) == 1
                        && route.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1)
                        && route.Costs.All(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1
                            || cost.Kind == ActionCostKind.Mana && cost.Amount == 0 && cost.ManaSymbols.IsEmpty);
                    if (route.Eligibility != SemanticKnowledge.Known || route.Production.Knowledge != SemanticKnowledge.Known
                        || route.Production.Alternatives.IsEmpty || !pureTap) {
                        uncertain = true; capacityKnown = false; continue;
                    }
                    foreach (var output in route.Production.Alternatives) {
                        var counts = Colors.Select(color => output.Colors.GetValueOrDefault(color)).ToArray();
                        long total = counts.Sum(count => (long)count);
                        if (total > 1024 || counts.Any(count => count < 0) || output.Colors.Keys.Any(color => !Colors.Contains(color))) {
                            uncertain = true; capacityKnown = false; continue;
                        }
                        physicalCapacity[source.SourceCardId] = Math.Max(physicalCapacity.GetValueOrDefault(source.SourceCardId), total);
                        if (total > 0) routes.Add(new(null, counts));
                    }
                }
                if (routes.Count > 0) sources.Add(new(source.SourceCardId, routes
                    .OrderBy(route => route.Output.Count(count => count > 0))
                    .ThenBy(route => string.Join(',', route.Output), StringComparer.Ordinal).ToImmutableArray()));
            }
        } else {
        ArgumentNullException.ThrowIfNull(frame);
        for (int color = 0; color < Colors.Length; color++) {
            int available = Math.Clamp(frame.State.Viewer!.Mana.GetValueOrDefault(Colors[color]), 0, 1024);
            // The provider certifies the next eligible unit of each color, not
            // that all later same-color units have the same spending restrictions.
            pool[color] = frame.Task == AiTask.Payment
                ? frame.Domain.PoolManaColors.Contains(Colors[color]) ? Math.Min(1, available) : 0 : available;
            if (frame.Task == AiTask.Payment && available > pool[color]) uncertain = true;
        }
        var metadata = frame.Domain.Actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        foreach (var set in frame.SourceActions) {
            if (set.SourceCardId == reservedSource) continue;
            var card = CardViews.FindActionSource(frame.State, set.SourceCardKey)?.Card;
            if (card == null) continue;
            var routes = ImmutableArray.CreateBuilder<Route>();
            bool tap = false, untap = false;
            foreach (var offered in set.Actions) {
                if (!metadata.TryGetValue(offered.Id, out var action) || !action.ManaAbility
                    || action.Affordability == ActionAffordability.Unavailable
                    || action.PaymentManaColors is { IsEmpty: true }) continue;
                // A paid filter can have an unmodeled funding dependency and
                // still have a finite nominal output bound. Additional costs
                // cannot increase that output. All alternatives consume the
                // same physical tap, so count their maximum, never their sum.
                bool consumesTap = action.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount > 0)
                    && !action.Costs.Any(cost => cost.Kind == ActionCostKind.Untap);
                if (!consumesTap || action.Production.Knowledge != SemanticKnowledge.Known || action.Production.Alternatives.IsEmpty)
                    capacityKnown = false;
                else physicalCapacity[set.SourceCardId] = Math.Max(physicalCapacity.GetValueOrDefault(set.SourceCardId),
                    action.Production.Alternatives.Max(output => output.Colors.Values.Sum(count => (long)count)));
                if (action.Production.Knowledge != SemanticKnowledge.Known || action.Production.Alternatives.IsEmpty
                    || action.Costs.Any(cost => !(cost.Kind == ActionCostKind.Mana && cost.Amount == 0 && cost.ManaSymbols.IsEmpty)
                        && (cost.Kind is not (ActionCostKind.Tap or ActionCostKind.Untap) || !cost.FromSource))) { uncertain = true; continue; }
                bool taps = action.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource);
                bool untaps = action.Costs.Any(cost => cost.Kind == ActionCostKind.Untap && cost.FromSource);
                tap |= taps; untap |= untaps;
                // One-use assignment is not a complete upper bound for free
                // or reversible producers. Keep any found finite witness, but
                // do not certify a shortfall when reuse is unmodeled.
                if (!taps || untaps || tap && untap) unmodeledReuse = true;
                foreach (var output in action.Production.Alternatives) {
                    var counts = Colors.Select(color => output.Colors.GetValueOrDefault(color)).ToArray();
                    if (counts.Any(count => count < 0) || counts.Sum(count => (long)count) > 1024
                        || output.Colors.Keys.Any(color => !Colors.Contains(color))) { uncertain = true; continue; }
                    // A disclosed zero bundle is complete knowledge, including
                    // when a variable producer currently has no counters.
                    // It contributes no route and cannot invent another color.
                    if (counts.Sum() == 0) continue;
                    if (frame.Task == AiTask.Payment && action.PaymentManaColors is { } usable
                        && output.Colors.Any(pair => pair.Value > 0 && !usable.Contains(pair.Key))) {
                        // Keep the full bundle. An incidental unusable color is
                        // harmless, but none of its output may fund this bill.
                        for (int color = 0; color < Colors.Length; color++) if (!usable.Contains(Colors[color])) counts[color] = 0;
                    }
                    if (counts.Sum() == 0) continue;
                    routes.Add(new(new(set.SourceCardId, set.SourceCardKey, offered.Id, output.Colors), counts));
                }
            }
            if (routes.Count > 0) sources.Add(new(set.SourceCardId, routes
                .OrderBy(route => route.Output.Count(count => count > 0))
                .ThenBy(route => route.Step!.ActionId, StringComparer.Ordinal).ToImmutableArray()));
        }
        poolCapacity = Colors.Sum(color => Math.Max(0L, frame.State.Viewer!.Mana.GetValueOrDefault(color)));
        }
        long nominalCapacity = poolCapacity + physicalCapacity.Values.Sum();
        if (capacityKnown && billMinimumKnown && nominalCapacity < (long)generic + required.Count)
            return Result(AiManaPlanStatus.Shortfall, [], "mana-nominal-capacity-shortfall");
        // A source may have multiple offers/catalog rows; it still produces once
        // in this finite plan, regardless of the number of colors or routes.
        var groups = sources.GroupBy(source => source.Id, StringComparer.Ordinal)
            .Select(group => new Source(group.Key, group.SelectMany(source => source.Routes).ToImmutableArray()))
            .OrderBy(source => source.Routes.SelectMany(route => route.Output.Select((count, color) => (count, color)))
                .Where(pair => pair.count > 0).Select(pair => pair.color).Distinct().Count())
            .ThenBy(source => source.Id, StringComparer.Ordinal).ToArray();
        int possibleColors = 0;
        for (int color = 0; color < Colors.Length; color++)
            if (pool[color] > 0 || groups.Any(source => source.Routes.Any(route => route.Output[color] > 0))) possibleColors |= 1 << color;
        // Reuse can add more units, not an undisclosed new color. Incomplete
        // production or life/hybrid alternatives still prevent this inference.
        if (!uncertain && required.Any(need => need.GenericAlternative == 0 && (need.Mask & possibleColors) == 0))
            return Result(AiManaPlanStatus.Shortfall, [], "mana-missing-color");
        bool exhausted = false;
        var used = new bool[groups.Length];
        var steps = new List<AiManaStep>();
        ImmutableArray<AiManaStep> witness = [];
        var failed = new HashSet<string>(StringComparer.Ordinal);
        bool found = Search(required, generic, pool, (int[])pool.Clone());
        return found ? Result(billIncomplete ? AiManaPlanStatus.Unknown : AiManaPlanStatus.Funded, witness,
                billIncomplete ? "mana-incomplete-bill" : "mana-assignment-witness")
            : exhausted ? Result(AiManaPlanStatus.BudgetExceeded, [], "mana-assignment-budget")
            : Result(uncertain || unmodeledReuse ? AiManaPlanStatus.Unknown : AiManaPlanStatus.Shortfall, [],
                uncertain || unmodeledReuse ? "mana-unmodeled-resource" : "mana-assignment-shortfall");

        bool Search(List<Requirement> needs, int remainingGeneric, int[] inventory, int[] originalPool)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!budget.TrySpend()) { exhausted = true; return false; }
            if (needs.Count == 0 && remainingGeneric <= 0) { witness = steps.ToImmutableArray(); return true; }
            long upper = inventory.Sum(value => (long)value) + groups.Where((_, index) => !used[index])
                .Sum(source => (long)source.Routes.Max(route => route.Output.Sum()));
            if (upper < remainingGeneric + needs.Count) return false;
            string signature = string.Join(',', needs.Select(need => need.Mask + ":" + need.GenericAlternative)) + "|" + remainingGeneric
                + "|" + string.Join(',', inventory) + "|" + string.Join(',', used.Select((value, index) => value ? index : -1).Where(index => index >= 0));
            if (!failed.Add(signature)) return false;
            Requirement? need = needs.OrderBy(need => BitOperations.PopCount((uint)need.Mask))
                .ThenBy(need => Enumerable.Range(0, Colors.Length).Where(color => (need.Mask & 1 << color) != 0)
                    .Sum(color => inventory[color] + groups.Where((_, index) => !used[index])
                        .Count(source => source.Routes.Any(route => route.Output[color] > 0)))).FirstOrDefault();
            int mask = need?.Mask ?? 63;
            for (int color = 0; color < Colors.Length; color++) {
                if ((mask & 1 << color) == 0 || inventory[color] == 0) continue;
                int take = need == null ? Math.Min(remainingGeneric, inventory[color]) : 1;
                var nextNeeds = new List<Requirement>(needs); if (need != null) nextNeeds.Remove(need);
                int[] nextInventory = (int[])inventory.Clone(), nextOriginal = (int[])originalPool.Clone();
                nextInventory[color] -= take;
                int fromPool = Math.Min(nextOriginal[color], take); nextOriginal[color] -= fromPool;
                if (fromPool > 0) steps.Add(new AiManaStep.Pool(Colors[color], fromPool));
                if (Search(nextNeeds, remainingGeneric - (need == null ? take : 0), nextInventory, nextOriginal)) return true;
                if (fromPool > 0) steps.RemoveAt(steps.Count - 1);
                if (exhausted) return false;
            }
            if (need?.GenericAlternative > 0) {
                var alternateNeeds = new List<Requirement>(needs); alternateNeeds.Remove(need);
                if (Search(alternateNeeds, remainingGeneric + need.GenericAlternative, inventory, originalPool)) return true;
                if (exhausted) return false;
            }
            for (int source = 0; source < groups.Length; source++) {
                if (used[source]) continue;
                foreach (var route in groups[source].Routes) {
                    if (!Enumerable.Range(0, Colors.Length).Any(color => (mask & 1 << color) != 0 && route.Output[color] > 0)) continue;
                    var nextInventory = inventory.Select((count, color) => count + route.Output[color]).ToArray();
                    used[source] = true; if (route.Step != null) steps.Add(route.Step);
                    if (Search(needs, remainingGeneric, nextInventory, originalPool)) return true;
                    if (route.Step != null) steps.RemoveAt(steps.Count - 1); used[source] = false;
                    if (exhausted) return false;
                }
            }
            return false;
        }

        AiManaPlan Result(AiManaPlanStatus status, ImmutableArray<AiManaStep> plan, string reason)
            => new(status, plan, before - budget.Remaining, reason);
    }
}
