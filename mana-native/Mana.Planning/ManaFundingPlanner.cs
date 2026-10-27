using System.Collections.Immutable;
using System.Numerics;
using Mana.Contracts;

namespace Mana.Planning;

/// <summary>Bounded finite nominal assignment for AI and human previews. No executable action authority crosses this boundary.</summary>
public static class ManaFundingPlanner
{
    private static readonly string[] Colors = ["W", "U", "B", "R", "G", "C"];
    private sealed record Requirement(int Mask, int GenericAlternative = 0);
    private enum ResourceUse { Limited, Tap, Consume, TapAndConsume }
    private sealed record Route(ManaFundingStep.Source? Step, int[] Output, ResourceUse Use);
    private sealed record Source(string Id, ImmutableArray<Route> Routes);
    private readonly record struct Capacity(long Tap, long Consume, long TapAndConsume)
    {
        internal long Maximum => Math.Max(Tap + Consume, TapAndConsume);
        internal Capacity Include(ResourceUse use, long amount) => use switch {
            ResourceUse.Tap => this with { Tap = Math.Max(Tap, amount) },
            ResourceUse.Consume => this with { Consume = Math.Max(Consume, amount) },
            ResourceUse.TapAndConsume => this with { TapAndConsume = Math.Max(TapAndConsume, amount) }, _ => this
        };
    }

    public static bool IsFiniteOneUse(ImmutableArray<ActionCost> costs) =>
        costs.Count(cost => cost.Kind == ActionCostKind.Tap) <= 1
        && costs.Count(cost => cost.Kind == ActionCostKind.Sacrifice) <= 1
        && costs.Any(cost => cost.FromSource && cost.Amount == 1 && cost.Kind is ActionCostKind.Tap or ActionCostKind.Sacrifice)
        && costs.All(cost => cost.Kind == ActionCostKind.Mana && cost.Amount == 0 && cost.ManaSymbols.IsEmpty
            || cost.FromSource && cost.Amount == 1 && cost.Kind is ActionCostKind.Tap or ActionCostKind.Sacrifice);

    private static ResourceUse PhysicalUse(ImmutableArray<ActionCost> costs)
    {
        bool tap = costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1);
        bool consume = costs.Any(cost => cost.Kind == ActionCostKind.Sacrifice && cost.FromSource && cost.Amount == 1);
        return consume ? tap ? ResourceUse.TapAndConsume : ResourceUse.Consume
            : tap && !costs.Any(cost => cost.Kind == ActionCostKind.Untap) ? ResourceUse.Tap : ResourceUse.Limited;
    }

    public static ManaFundingPlan Evaluate(ManaFundingContext funding, IEnumerable<string> symbols,
        int generic = 0, int workLimit = 2048, string? reservedSource = null, CancellationToken cancellation = default)
    {
        if (workLimit <= 0) throw new ArgumentOutOfRangeException(nameof(workLimit));
        return Evaluate(funding, symbols, generic, new FixedWork(workLimit), reservedSource, cancellation);
    }

    public static ManaFundingPlan EvaluateBill(ManaFundingContext funding, IEnumerable<ManaBillPart> parts,
        int generic = 0, int workLimit = 2048, string? reservedSource = null, CancellationToken cancellation = default)
    {
        if (workLimit <= 0) throw new ArgumentOutOfRangeException(nameof(workLimit));
        return EvaluateBill(funding, parts, generic, new FixedWork(workLimit), reservedSource, cancellation);
    }

    // Counts remain exact in the observation. Only this bounded calculation
    // expands repeated colored shards, and never allocates from an unchecked
    // count. Generic quantities are multiplied directly under the bill limit.
    public static ManaFundingPlan EvaluateBill(ManaFundingContext funding, IEnumerable<ManaBillPart> parts,
        int generic, IManaPlanningWork budget, string? reservedSource = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(funding); ArgumentNullException.ThrowIfNull(parts); ArgumentNullException.ThrowIfNull(budget);
        int before = budget.Remaining, rows = 0;
        long demand = generic;
        if (demand is < 0 or > 1024) return Limited();
        var symbols = new List<string>();
        foreach (var part in parts) {
            cancellation.ThrowIfCancellationRequested();
            if (!budget.TrySpend()) return new(ManaFundingStatus.BudgetExceeded, [], before - budget.Remaining, "mana-bill-budget");
            if (++rows > 128 || part == null || string.IsNullOrWhiteSpace(part.Symbol) || part.Symbol.Length > 32 || part.Count <= 0)
                return Limited();
            if (long.TryParse(part.Symbol.Trim('{', '}'), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long quantity)) {
                if (quantity > 1024) return Limited();
                demand += quantity * part.Count;
                if (demand > 1024) return Limited();
            } else {
                if (part.Count > 128 - symbols.Count) return Limited();
                for (int index = 0; index < part.Count; index++) symbols.Add(part.Symbol);
            }
        }
        var plan = Evaluate(funding, symbols, (int)demand, budget, reservedSource, cancellation);
        return plan with { WorkSpent = before - budget.Remaining };

        ManaFundingPlan Limited() => new(ManaFundingStatus.Unknown, [], before - budget.Remaining, "mana-bill-bound");
    }

    public static ManaFundingPlan Evaluate(ManaFundingContext funding, IEnumerable<string> symbols,
        int generic, IManaPlanningWork budget, string? reservedSource = null,
        CancellationToken cancellation = default, long? poolCapacityUpperBound = null)
    {
        ArgumentNullException.ThrowIfNull(funding); ArgumentNullException.ThrowIfNull(symbols); ArgumentNullException.ThrowIfNull(budget);
        int before = budget.Remaining;
        var required = new List<Requirement>();
        bool uncertain = false, billIncomplete = false, unmodeledReuse = false, billMinimumKnown = true;
        foreach (string symbol in symbols) {
            cancellation.ThrowIfCancellationRequested();
            string value = symbol.Trim('{', '}');
            if (int.TryParse(value, out int amount) && amount >= 0) {
                if ((long)generic + amount > 1024) return Result(ManaFundingStatus.Unknown, [], "mana-bill-bound");
                generic += amount; continue;
            }
            string[] parts = value.Split('/');
            int mask = 0, alternative = 0;
            foreach (string part in parts) {
                int index = Array.IndexOf(Colors, part);
                if (index >= 0) mask |= 1 << index;
                else if (int.TryParse(part, out int hybridGeneric) && hybridGeneric > 0) {
                    if (hybridGeneric > 1024) return Result(ManaFundingStatus.Unknown, [], "mana-bill-bound");
                    alternative = hybridGeneric;
                }
                else { uncertain = true; billMinimumKnown = false; } // X, snow, life and other alternatives remain explicit unknowns.
            }
            if (mask > 0) required.Add(new(mask, alternative));
            else billIncomplete = true;
        }
        if (generic < 0 || generic > 1024 || required.Count > 128)
            return Result(ManaFundingStatus.Unknown, [], "mana-bill-bound");

        uncertain |= funding.Knowledge != SemanticKnowledge.Known;
        if (funding.Knowledge == SemanticKnowledge.Unsupported) return Result(ManaFundingStatus.Unknown, [], "numeric-funding-unsupported");
        var pool = Colors.Select(color => Math.Clamp(funding.Pool.GetValueOrDefault(color), 0, 1024)).ToArray();
        long poolCapacity = Math.Max(pool.Sum(value => (long)value), poolCapacityUpperBound ?? 0);
        uncertain |= poolCapacity > pool.Sum(value => (long)value);
        var sources = new List<Source>();
        bool capacityKnown = funding.Knowledge == SemanticKnowledge.Known;
        var physicalCapacity = new Dictionary<string, Capacity>(StringComparer.Ordinal);
        foreach (var source in funding.Sources.OrderBy(source => source.SourceCardId, StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();
            if (!budget.TrySpend()) return Result(ManaFundingStatus.BudgetExceeded, [], "mana-resource-budget");
            if (source.SourceCardId == reservedSource) continue;
            var routes = ImmutableArray.CreateBuilder<Route>();
            bool tap = false, untap = false;
            for (int routeIndex = 0; routeIndex < source.Routes.Length; routeIndex++) {
                cancellation.ThrowIfCancellationRequested();
                if (!budget.TrySpend()) return Result(ManaFundingStatus.BudgetExceeded, [], "mana-resource-budget");
                var route = source.Routes[routeIndex];
                var physicalUse = PhysicalUse(route.Costs);
                if (route.Eligibility != SemanticKnowledge.Known || physicalUse == ResourceUse.Limited
                    || route.Production.Knowledge != SemanticKnowledge.Known || route.Production.Alternatives.IsEmpty)
                    capacityKnown = false;
                else physicalCapacity[source.SourceCardId] = physicalCapacity.GetValueOrDefault(source.SourceCardId).Include(physicalUse,
                    route.Production.Alternatives.Max(output => output.Colors.Values.Sum(count => (long)count)));
                bool oneUse = IsFiniteOneUse(route.Costs);
                if (route.Eligibility != SemanticKnowledge.Known || route.Production.Knowledge != SemanticKnowledge.Known
                    || route.Production.Alternatives.IsEmpty || !oneUse && route.Costs.Any(cost =>
                        !(cost.Kind == ActionCostKind.Mana && cost.Amount == 0 && cost.ManaSymbols.IsEmpty)
                        && (cost.Kind is not (ActionCostKind.Tap or ActionCostKind.Untap) || !cost.FromSource || cost.Amount != 1))) {
                    uncertain = true; continue;
                }
                bool taps = route.Costs.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource);
                bool untaps = route.Costs.Any(cost => cost.Kind == ActionCostKind.Untap && cost.FromSource);
                tap |= taps; untap |= untaps;
                if (!oneUse && (!taps || untaps || tap && untap)) unmodeledReuse = true;
                for (int alternativeIndex = 0; alternativeIndex < route.Production.Alternatives.Length; alternativeIndex++) {
                    var output = route.Production.Alternatives[alternativeIndex];
                    var counts = Colors.Select(color => output.Colors.GetValueOrDefault(color)).ToArray();
                    if (counts.Any(count => count < 0) || counts.Sum(count => (long)count) > 1024
                        || output.Colors.Keys.Any(color => !Colors.Contains(color))) { uncertain = true; continue; }
                    if (counts.Sum() == 0) continue;
                    routes.Add(new(new(source.SourceCardId, routeIndex, alternativeIndex, output.Colors), counts,
                        oneUse ? physicalUse : ResourceUse.Limited));
                }
            }
            if (routes.Count > 0) sources.Add(new(source.SourceCardId, routes
                .OrderBy(route => route.Use is ResourceUse.Consume or ResourceUse.TapAndConsume ? 1 : 0)
                .ThenBy(route => route.Output.Count(count => count > 0))
                .ThenBy(route => route.Step!.RouteIndex).ThenBy(route => route.Step!.AlternativeIndex).ToImmutableArray()));
        }
        long nominalCapacity = poolCapacity + physicalCapacity.Values.Sum(capacity => capacity.Maximum);
        if (capacityKnown && billMinimumKnown && nominalCapacity < (long)generic + required.Count)
            return Result(ManaFundingStatus.Shortfall, [], "mana-nominal-capacity-shortfall");
        // Each physical source owns a finite resource state, shared by all its
        // routes. Tapping can precede consuming; consuming prevents later use.
        var groups = sources.GroupBy(source => source.Id, StringComparer.Ordinal)
            .Select(group => new Source(group.Key, group.SelectMany(source => source.Routes).ToImmutableArray()))
            .OrderBy(source => source.Routes.All(route => route.Use is ResourceUse.Consume or ResourceUse.TapAndConsume) ? 1 : 0)
            .ThenBy(source => source.Routes.SelectMany(route => route.Output.Select((count, color) => (count, color)))
                .Where(pair => pair.count > 0).Select(pair => pair.color).Distinct().Count())
            .ThenBy(source => source.Id, StringComparer.Ordinal).ToArray();
        int possibleColors = 0;
        for (int color = 0; color < Colors.Length; color++)
            if (pool[color] > 0 || groups.Any(source => source.Routes.Any(route => route.Output[color] > 0))) possibleColors |= 1 << color;
        // Reuse can add more units, not an undisclosed new color. Incomplete
        // production or life/hybrid alternatives still prevent this inference.
        if (!uncertain && required.Any(need => need.GenericAlternative == 0 && (need.Mask & possibleColors) == 0))
            return Result(ManaFundingStatus.Shortfall, [], "mana-missing-color");
        bool exhausted = false;
        var used = new int[groups.Length]; // 0 available, 1 tapped, 2 consumed, 3 unmodeled one-use
        var steps = new List<ManaFundingStep>();
        ImmutableArray<ManaFundingStep> witness = [];
        var failed = new HashSet<string>(StringComparer.Ordinal);
        bool found = Search(required, generic, pool, (int[])pool.Clone());
        return found ? Result(billIncomplete ? ManaFundingStatus.Unknown : ManaFundingStatus.Funded, witness,
                billIncomplete ? "mana-incomplete-bill" : "mana-assignment-witness")
            : exhausted ? Result(ManaFundingStatus.BudgetExceeded, [], "mana-assignment-budget")
            : Result(uncertain || unmodeledReuse ? ManaFundingStatus.Unknown : ManaFundingStatus.Shortfall, [],
                uncertain || unmodeledReuse ? "mana-unmodeled-resource" : "mana-assignment-shortfall");

        bool Search(List<Requirement> needs, int remainingGeneric, int[] inventory, int[] originalPool)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!budget.TrySpend()) { exhausted = true; return false; }
            if (needs.Count == 0 && remainingGeneric <= 0) { witness = steps.ToImmutableArray(); return true; }
            long upper = inventory.Sum(value => (long)value) + groups.Select((source, index) => Remaining(source, used[index])).Sum();
            if (upper < remainingGeneric + needs.Count) return false;
            string signature = string.Join(',', needs.Select(need => need.Mask + ":" + need.GenericAlternative)) + "|" + remainingGeneric
                + "|" + string.Join(',', inventory) + "|" + string.Join(',', used);
            if (!failed.Add(signature)) return false;
            Requirement? need = needs.OrderBy(need => BitOperations.PopCount((uint)need.Mask))
                .ThenBy(need => Enumerable.Range(0, Colors.Length).Where(color => (need.Mask & 1 << color) != 0)
                    .Sum(color => inventory[color] + groups.Where((source, index) => source.Routes.Any(route => Next(used[index], route.Use) >= 0 && route.Output[color] > 0))
                        .Count())).FirstOrDefault();
            int mask = need?.Mask ?? 63;
            int productive = remainingGeneric > 0 ? 63 : needs.Aggregate(0, (colors, remaining) => colors | remaining.Mask);
            for (int color = 0; color < Colors.Length; color++) {
                if ((mask & 1 << color) == 0 || inventory[color] == 0) continue;
                int take = need == null ? Math.Min(remainingGeneric, inventory[color]) : 1;
                var nextNeeds = new List<Requirement>(needs); if (need != null) nextNeeds.Remove(need);
                int[] nextInventory = (int[])inventory.Clone(), nextOriginal = (int[])originalPool.Clone();
                nextInventory[color] -= take;
                int fromPool = Math.Min(nextOriginal[color], take); nextOriginal[color] -= fromPool;
                if (fromPool > 0) steps.Add(new ManaFundingStep.Pool(Colors[color], fromPool));
                if (Search(nextNeeds, remainingGeneric - (need == null ? take : 0), nextInventory, nextOriginal)) return true;
                if (fromPool > 0) steps.RemoveAt(steps.Count - 1);
                if (exhausted) return false;
            }
            if (need?.GenericAlternative > 0) {
                var alternateNeeds = new List<Requirement>(needs); alternateNeeds.Remove(need);
                if (Search(alternateNeeds, remainingGeneric + need.GenericAlternative, inventory, originalPool)) return true;
                if (exhausted) return false;
            }
            // Satisfy the selected colored need across all physical sources
            // before preparing generic or other colored demand. The second
            // pass remains necessary: tapping for generic can precede consuming
            // that same source for the selected color.
            for (int pass = 0; pass < (need == null ? 1 : 2); pass++)
            for (int source = 0; source < groups.Length; source++) {
                foreach (var route in groups[source].Routes) {
                    int next = Next(used[source], route.Use);
                    if (next < 0) continue;
                    bool matches = Enumerable.Range(0, Colors.Length).Any(color => (mask & 1 << color) != 0 && route.Output[color] > 0);
                    if (need != null && matches != (pass == 0)) continue;
                    if (!Enumerable.Range(0, Colors.Length).Any(color => (productive & 1 << color) != 0 && route.Output[color] > 0)) continue;
                    var nextInventory = inventory.Select((count, color) => count + route.Output[color]).ToArray();
                    int previous = used[source]; used[source] = next; if (route.Step != null) steps.Add(route.Step);
                    if (Search(needs, remainingGeneric, nextInventory, originalPool)) return true;
                    if (route.Step != null) steps.RemoveAt(steps.Count - 1); used[source] = previous;
                    if (exhausted) return false;
                }
            }
            return false;
        }

        ManaFundingPlan Result(ManaFundingStatus status, ImmutableArray<ManaFundingStep> plan, string reason)
            => new(status, plan, before - budget.Remaining, reason);

        static int Next(int state, ResourceUse use) => use switch {
            ResourceUse.Tap when state == 0 => 1,
            ResourceUse.Consume when state is 0 or 1 => 2,
            ResourceUse.TapAndConsume when state == 0 => 2,
            ResourceUse.Limited when state == 0 => 3, _ => -1
        };

        static long Remaining(Source source, int state)
        {
            var capacity = new Capacity();
            long limited = 0;
            foreach (var route in source.Routes) {
                if (Next(state, route.Use) < 0) continue;
                long amount = route.Output.Sum(value => (long)value);
                if (route.Use == ResourceUse.Limited) limited = Math.Max(limited, amount);
                else capacity = capacity.Include(route.Use, amount);
            }
            return Math.Max(capacity.Maximum, limited);
        }
    }
    private sealed class FixedWork(int units) : IManaPlanningWork
    {
        public int Remaining { get; private set; } = units;
        public bool TrySpend(int amount = 1)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (Remaining < amount) return false;
            Remaining -= amount; return true;
        }
    }
}
