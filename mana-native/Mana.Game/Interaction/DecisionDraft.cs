using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table.Interaction;

/// <summary>Unsubmitted choices belong to one current decision, never to a frame.</summary>
internal sealed class DecisionDraft
{
    private (string Game, int Viewer, string Decision)? scope;
    private readonly List<int> selections = [];
    private int[] allocations = [];
    private readonly IReadOnlyList<int> selectedView;
    private IReadOnlyList<int> allocationView = Array.Empty<int>();
    private Decision? observedDecision;
    internal DecisionDraft() => selectedView = selections.AsReadOnly();
    internal IReadOnlyList<int> Selections => selectedView;
    internal IReadOnlyList<int> Allocations => allocationView;

    internal bool Observe(GameSnapshot? state, bool direct)
    {
        var current = state?.Decision == null ? ((string, int, string)?)null : (state.Id, state.ViewerId, state.Decision.Id);
        bool changed = scope != current || !SameAllocation(observedDecision, state?.Decision);
        observedDecision = state?.Decision;
        if (!changed) return false;
        scope = current; selections.Clear(); ReplaceAllocations([]);
        if (state?.Decision is { } decision) {
            if (!direct && decision.Ordered && decision.Min == decision.Choices.Length)
                selections.AddRange(decision.Choices.Select(choice => choice.Index));
            ReplaceAllocations(decision.Task == DecisionTask.Allocation
                ? decision.Choices.Select((_, index) => AllocationGuide.Minimum(decision, index)).ToArray()
                : decision.Choices.Select(_ => decision.AtLeastOne ? 1 : 0).ToArray());
        }
        return true;
    }

    internal void Clear() { scope = null; observedDecision = null; selections.Clear(); ReplaceAllocations([]); }
    internal int Position(int index) => selections.IndexOf(index) + 1;
    internal void ClearSelection() => selections.Clear();
    internal bool Toggle(GameSnapshot state, int index)
    {
        if (!Matches(state) || state.Decision is not { } decision
            || !(decision.LibraryCards.Length > 0 ? decision.LibraryCards.Any(choice => choice.Index == index)
                : decision.Choices.Any(choice => choice.Index == index))) return false;
        if (selections.Remove(index)) return true;
        if (decision.Max == 1) selections.Clear();
        if (selections.Count >= decision.Max) return false;
        selections.Add(index); return true;
    }

    internal void ResetAllocation(GameSnapshot state)
    {
        if (!Matches(state) || state.Decision is not { Task: DecisionTask.Allocation } decision) return;
        ReplaceAllocations(decision.Choices.Select((_, index) => AllocationGuide.Minimum(decision, index)).ToArray());
    }
    internal bool SetAllocation(GameSnapshot state, int index, int amount)
    {
        if (!Matches(state) || state.Decision is not { Task: DecisionTask.Allocation } decision || index < 0 || index >= allocations.Length
            || amount < AllocationGuide.Minimum(decision, index) || amount > AllocationGuide.Maximum(decision, index)) return false;
        allocations[index] = amount; return true;
    }
    internal void AdjustAllocation(GameSnapshot state, int index, int delta)
    {
        if (!Matches(state) || state.Decision is not { Task: DecisionTask.Allocation } decision || index < 0 || index >= allocations.Length) return;
        if (delta > 0 && AllocationGuide.Total(allocations) >= decision.Amount) return;
        long amount = (long)allocations[index] + delta;
        if (amount is >= int.MinValue and <= int.MaxValue) SetAllocation(state, index, (int)amount);
    }
    internal void AssignRemaining(GameSnapshot state, int index)
    {
        if (!Matches(state) || state.Decision is not { Task: DecisionTask.Allocation } decision || index < 0 || index >= allocations.Length) return;
        long amount = Math.Min(AllocationGuide.Maximum(decision, index), Math.Max(allocations[index],
            (long)allocations[index] + Math.Max(0, decision.Amount - AllocationGuide.Total(allocations))));
        SetAllocation(state, index, (int)amount);
    }
    internal DecisionCommand? AllocationReply(GameSnapshot state) => Matches(state) ? AllocationGuide.Reply(state, allocations) : null;
    private bool Matches(GameSnapshot state) => state.Decision != null && scope == (state.Id, state.ViewerId, state.Decision.Id)
        && SameAllocation(observedDecision, state.Decision);
    private void ReplaceAllocations(int[] values) { allocations = values; allocationView = Array.AsReadOnly(values); }
    private static bool SameAllocation(Decision? before, Decision? after)
    {
        if (before?.Task != DecisionTask.Allocation && after?.Task != DecisionTask.Allocation) return true;
        if (before == null || after == null || before.Task != after.Task || before.Amount != after.Amount
            || before.AllocationKind != after.AllocationKind || before.AtLeastOne != after.AtLeastOne || before.MaySkip != after.MaySkip
            || before.Choices.Length != after.Choices.Length || !before.Limits.SequenceEqual(after.Limits)) return false;
        for (int index = 0; index < before.Choices.Length; index++) {
            var old = before.Choices[index]; var current = after.Choices[index];
            if (old.Index != current.Index || old.Id != current.Id || old.CardId != current.CardId || old.PlayerId != current.PlayerId
                || old.Card?.VisualId != current.Card?.VisualId || old.Lethal != current.Lethal || old.IsDefender != current.IsDefender
                || AllocationGuide.Minimum(before, index) != AllocationGuide.Minimum(after, index)
                || AllocationGuide.Maximum(before, index) != AllocationGuide.Maximum(after, index)) return false;
        }
        return true;
    }
}
