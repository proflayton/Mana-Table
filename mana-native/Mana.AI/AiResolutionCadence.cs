using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

// Public occurrence evidence only. No faces, names, costs, private offers or
// policy answers enter the cadence owner.
public sealed record AiCadenceTarget(string Kind, string Id, int? PlayerId);
public sealed record AiCadenceMember(string StackId, string ExecutionId, StackActionKind Kind,
    int? ControllerId, StackRepetition? Repetition, ImmutableArray<AiCadenceTarget> Targets);
public sealed record AiCadenceExecution(string Id, string? ParentId, string RootId, ExecutionKind Kind,
    int? ActorId, string? SourceId, ExecutionStep Step, ExecutionOutcome? Outcome);
public sealed record AiCadenceEvent(long Id, string ExecutionId)
{
    // Announcement classification only. The exact event remains in every
    // cohort/ancestry guard, including a mana ability with other consequences.
    internal bool ManaTapCost { get; init; }
}
public sealed record AiResolutionProgress(string PhaseKey, long ActionVersion, long ChoiceVersion,
    ImmutableArray<AiCadenceMember> Queue, ImmutableArray<AiCadenceExecution> Executions,
    ImmutableArray<AiCadenceEvent> Events)
{
    internal static AiResolutionProgress? From(GameSnapshot state)
    {
        if (state.StackActionVersion is not >= 0 || state.ResolutionChoiceVersion is not >= 0 || state.Executions == null
            || state.Stack.Length > 256 || state.Activity.Length > 4096) return null;
        // Only already disclosed stack occurrences and actual public activity
        // can admit links. A bot's private preparation frame cannot create a
        // public cadence cohort or provide an otherwise unknown ancestor.
        var publicIds = state.Stack.Select(item => item.ExecutionId)
            .Concat(state.Activity.Select(item => item.ExecutionId)).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        var executions = state.Executions.Occurrences.Where(item => publicIds.Contains(item.Id)).Select(item => new AiCadenceExecution(
            item.Id, item.ParentId != null && publicIds.Contains(item.ParentId) ? item.ParentId : null,
            publicIds.Contains(item.BudgetRootId) ? item.BudgetRootId : item.Id, item.Kind, item.ActorPlayerId,
            item.SourceCardId, item.Step, item.Outcome)).ToImmutableArray();
        var mana = executions.GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).Where(item => item is { Kind: ExecutionKind.Mana, Step: ExecutionStep.Complete,
                Outcome: ExecutionOutcome.Resolved, ActorId: not null, SourceId: { Length: > 0 } })
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        return new(state.PhaseKey, state.StackActionVersion.Value, state.ResolutionChoiceVersion.Value,
            state.Stack.Select(item => new AiCadenceMember(item.Id, item.ExecutionId, item.Kind, item.ControllerId, item.Repetition,
                item.Targets.Select(target => new AiCadenceTarget(target.Kind, target.Id, target.PlayerId)).ToImmutableArray())).ToImmutableArray(),
            executions, state.Activity.Select(item => new AiCadenceEvent(item.Id, item.ExecutionId) {
                ManaTapCost = item is { Kind: "tapped", ExecutionStep: ExecutionStep.Cost,
                    Change: { Kind: "state", Property: "tapped", Before: 0, After: 1 } }
                    && mana.TryGetValue(item.ExecutionId, out var execution)
                    && item.PlayerId == execution.ActorId && item.CardId == execution.SourceId
            }).ToImmutableArray());
    }
}

// One bounded, immutable original queue with monotonic completion bookkeeping.
// This owns elapsed announcement time, never consent or command authority.
internal sealed class AiResolutionCadence
{
    private readonly AiResolutionProgress original;
    private readonly ImmutableDictionary<string, AiCadenceExecution> members;
    private readonly ImmutableHashSet<string> repeated;
    private readonly Dictionary<string, ExecutionOutcome> completed = new(StringComparer.Ordinal);
    private HashSet<string> remaining;
    private long activity;
    private bool ended;
    private AiResolutionProgress last;

    private AiResolutionCadence(AiResolutionProgress progress, ImmutableHashSet<string> repeatedIds)
    {
        original = last = progress; repeated = repeatedIds;
        var indexed = progress.Executions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        members = progress.Queue.ToImmutableDictionary(item => item.ExecutionId, item => indexed[item.ExecutionId], StringComparer.Ordinal);
        remaining = progress.Queue.Select(item => item.StackId).ToHashSet(StringComparer.Ordinal);
        activity = progress.Events.Select(item => item.Id).DefaultIfEmpty().Max();
    }

    internal static AiResolutionCadence? Capture(AiResolutionProgress? progress)
    {
        if (!Valid(progress) || progress!.Queue.Length is < 2 or > 256) return null;
        var indexed = progress.Executions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (progress.Queue.Any(item => !indexed.TryGetValue(item.ExecutionId, out var execution) || execution.Step == ExecutionStep.Complete)) return null;
        var repeated = progress.Queue.Where(item => item.Kind == StackActionKind.TriggeredAbility && item.Repetition != null)
            .GroupBy(item => item.Repetition).Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.ExecutionId))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (repeated.IsEmpty) return null;
        return new(progress, repeated);
    }

    internal bool Continue(AiResolutionProgress? progress)
    {
        if (ended || !Valid(progress) || progress!.PhaseKey != original.PhaseKey
            || progress.ActionVersion != original.ActionVersion || progress.ChoiceVersion != original.ChoiceVersion) return false;
        var indexed = progress.Executions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var previousExecutions = last.Executions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var execution in progress.Executions)
            if ((!previousExecutions.TryGetValue(execution.Id, out var prior) || prior != execution) && !Includes(execution.Id, indexed)) return false;
        foreach (var pair in members) {
            if (!indexed.TryGetValue(pair.Key, out var current)) {
                if (!completed.ContainsKey(pair.Key)) return false;
                continue;
            }
            var before = pair.Value;
            // Public event tails can stop disclosing an ancestor while this
            // exact captured occurrence remains. Keep its originally admitted
            // immutable evidence; a new known parent/root still contradicts it.
            // This retention never supplies links for newly arrived children:
            // Includes follows only their currently disclosed parent chain.
            if (current.ParentId != null && current.ParentId != before.ParentId
                || current.RootId != current.Id && current.RootId != before.RootId || current.Kind != before.Kind
                || current.ActorId != before.ActorId || current.SourceId != before.SourceId) return false;
            if (current is { Step: ExecutionStep.Complete, Outcome: { } outcome }) {
                if (completed.TryGetValue(pair.Key, out var prior) && prior != outcome) return false;
                completed[pair.Key] = outcome;
            }
        }
        int position = -1;
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in progress.Queue) {
            int index = -1;
            for (int i = 0; i < original.Queue.Length; i++) if (original.Queue[i].StackId == item.StackId) { index = i; break; }
            if (index <= position || index < 0 || !remaining.Contains(item.StackId) || !next.Add(item.StackId)
                || !Same(original.Queue[index], item)) return false;
            position = index;
        }
        // An ordinary spell in the captured surroundings keeps its own beat.
        if (original.Queue.Any(item => remaining.Contains(item.StackId) && !next.Contains(item.StackId) && !repeated.Contains(item.ExecutionId))) return false;
        foreach (var entry in progress.Events.Where(item => item.Id > activity)) if (!Includes(entry.ExecutionId, indexed)) return false;
        remaining = next;
        last = progress;
        activity = Math.Max(activity, progress.Events.Select(item => item.Id).DefaultIfEmpty().Max());
        ended = repeated.All(completed.ContainsKey);
        return true;
    }

    private bool Includes(string id, Dictionary<string, AiCadenceExecution> indexed)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(id)) {
            if (repeated.Contains(id)) return true;
            if (!indexed.TryGetValue(id, out var execution) || execution.ParentId is not { } parent) return false;
            id = parent;
        }
        return false;
    }

    internal static bool Changed(AiResolutionProgress? before, AiResolutionProgress? after)
        => before == null != (after == null) || before != null && after != null
            && (before.PhaseKey != after.PhaseKey || before.ActionVersion != after.ActionVersion || before.ChoiceVersion != after.ChoiceVersion
                || PublicProgressChanged(before, after));

    // Scope guards still end a captured cohort, but an empty phase or the
    // bot's own cost input is not another public action to announce. Missing or
    // contradictory typed evidence keeps the conservative ordinary comparison.
    internal static bool AnnouncementChanged(AiResolutionProgress? before, AiResolutionProgress? after)
    {
        if (!Valid(before) || !Valid(after)) return Changed(before, after);
        var beforeCosts = ManaTapCosts(before!);
        var afterCosts = ManaTapCosts(after!);
        if (before!.Queue.Length != after!.Queue.Length || !before.Queue.Zip(after.Queue).All(pair => Same(pair.First, pair.Second))
            || !before.Events.Where(item => !beforeCosts.Contains(item.ExecutionId)).SequenceEqual(after.Events.Where(item => !afterCosts.Contains(item.ExecutionId))))
            return true;
        using var prior = before.Executions.Where(item => !beforeCosts.Contains(item.Id)).GetEnumerator();
        using var next = after.Executions.Where(item => !afterCosts.Contains(item.Id)).GetEnumerator();
        while (prior.MoveNext()) {
            if (!next.MoveNext()) return true;
            if (prior.Current == next.Current) continue;
            // The already announced stack occurrence can ask for an internal
            // target, cost or resolution choice. Those steps do not announce
            // another action. Preserve only this exact known queued identity;
            // public effects, terminals and unqueued progress keep their beat.
            var left = prior.Current;
            var right = next.Current;
            bool internalStep = left.Outcome == null && right.Outcome == null
                && left.ActorId != null && !string.IsNullOrEmpty(left.SourceId)
                && left.Step is ExecutionStep.Stack or ExecutionStep.Resolution or ExecutionStep.Choice or ExecutionStep.Cost
                && right.Step is ExecutionStep.Stack or ExecutionStep.Resolution or ExecutionStep.Choice or ExecutionStep.Cost
                && (left with { Step = right.Step }) == right
                && before.Queue.Any(item => item.ExecutionId == left.Id && item.ControllerId == left.ActorId
                    && (item.Kind == StackActionKind.Spell && left.Kind == ExecutionKind.Spell
                        || item.Kind == StackActionKind.ActivatedAbility && left.Kind == ExecutionKind.Activation
                        || item.Kind == StackActionKind.TriggeredAbility && left.Kind == ExecutionKind.Trigger));
            if (!internalStep) return true;
        }
        return next.MoveNext();
    }

    private static HashSet<string> ManaTapCosts(AiResolutionProgress progress)
    {
        var queued = progress.Queue.Select(item => item.ExecutionId).ToHashSet(StringComparer.Ordinal);
        // A matching tap is insufficient when the same real mana execution
        // also deals damage, changes life, or supplies any unclassified fact.
        // Missing event/actor/source/step evidence never enters this set.
        return progress.Events.GroupBy(item => item.ExecutionId, StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && !queued.Contains(group.Key) && group.All(item => item.ManaTapCost))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static bool PublicProgressChanged(AiResolutionProgress before, AiResolutionProgress after)
        => before.Queue.Length != after.Queue.Length || !before.Queue.Zip(after.Queue).All(pair => Same(pair.First, pair.Second))
            || !before.Executions.SequenceEqual(after.Executions) || !before.Events.SequenceEqual(after.Events);

    private static bool Same(AiCadenceMember before, AiCadenceMember after)
        => before.StackId == after.StackId && before.ExecutionId == after.ExecutionId && before.Kind == after.Kind
            && before.ControllerId == after.ControllerId && before.Repetition == after.Repetition && before.Targets.SequenceEqual(after.Targets);

    private static bool Valid(AiResolutionProgress? value)
    {
        if (value == null || value.ActionVersion < 0 || value.ChoiceVersion < 0 || value.Queue.IsDefault || value.Executions.IsDefault
            || value.Events.IsDefault || value.Queue.Length > 256 || value.Executions.Length > 4096 || value.Events.Length > 4096
            || value.Queue.Any(item => item == null || string.IsNullOrWhiteSpace(item.StackId) || string.IsNullOrWhiteSpace(item.ExecutionId)
                || item.Targets.IsDefault || item.Targets.Any(target => target == null))
            || value.Executions.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id))
            || value.Events.Any(item => item == null || item.Id < 0 || item.ExecutionId == null)
            || value.Queue.Select(item => item.StackId).Distinct().Count() != value.Queue.Length
            || value.Queue.Select(item => item.ExecutionId).Distinct().Count() != value.Queue.Length
            || value.Executions.Select(item => item.Id).Distinct().Count() != value.Executions.Length) return false;
        var indexed = value.Executions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return value.Queue.All(item => item.Repetition == null || item.Kind == StackActionKind.TriggeredAbility
            && !string.IsNullOrWhiteSpace(item.Repetition.GroupKey) && !string.IsNullOrWhiteSpace(item.Repetition.SourceCardId)
            && indexed.TryGetValue(item.ExecutionId, out var execution) && execution.Kind == ExecutionKind.Trigger
            && execution.SourceId == item.Repetition.SourceCardId && execution.ActorId == item.ControllerId);
    }
}
