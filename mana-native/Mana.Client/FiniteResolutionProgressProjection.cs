using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>
/// Owner-thread progress for one captured queue. Call Observe only after the
/// session accepts a publication; this projection never reads the worker policy
/// or authorizes commands. Proven terminal outcomes survive history-tail eviction.
/// </summary>
public sealed class FiniteResolutionProgressProjection
{
    internal sealed record Member(string ExecutionId, string RootId, string? ParentId, ExecutionKind Kind,
        int? ActorId, string? SourceId);
    private readonly string game;
    private readonly int viewer;
    private readonly ImmutableDictionary<string, Member> members;
    private readonly ImmutableHashSet<string> captured;
    private readonly ImmutableHashSet<string>? decline;
    private ImmutableDictionary<string, ExecutionOutcome> outcomes = ImmutableDictionary<string, ExecutionOutcome>.Empty;
    private long revision;

    internal FiniteResolutionProgressProjection(string game, int viewer, long captureRevision, ImmutableArray<Member> members,
        ImmutableHashSet<string>? decline)
    {
        this.game = game; this.viewer = viewer; revision = captureRevision;
        this.members = members.ToImmutableDictionary(member => member.ExecutionId, StringComparer.Ordinal);
        captured = this.members.Keys.ToImmutableHashSet(StringComparer.Ordinal); this.decline = decline;
        Progress = Summary(this.captured, []);
        DeclineProgress = decline == null ? null : Summary(decline, []);
    }

    public FiniteResolutionProgress Progress { get; private set; }
    public FiniteResolutionProgress? DeclineProgress { get; private set; }

    public bool Observe(GameSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Id != game || state.ViewerId != viewer || state.Revision < 0 || state.Revision < revision) return false;
        try { ExecutionFrames.Validate(state); }
        catch (ArgumentException) { return false; }
        var next = outcomes;
        var unknown = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var occurrences = state.Executions?.Occurrences.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (string id in captured) {
            if (occurrences == null || !occurrences.TryGetValue(id, out var actual)) {
                if (!next.ContainsKey(id)) unknown.Add(id);
                continue;
            }
            var original = members[id];
            if (actual.BudgetRootId != original.RootId || actual.ParentId != original.ParentId || actual.Kind != original.Kind
                || actual.ActorPlayerId != original.ActorId || actual.SourceCardId != original.SourceId) return false;
            if (next.TryGetValue(id, out var previous)
                && (actual.Step != ExecutionStep.Complete || actual.Outcome != previous)) return false;
            if (actual is { Step: ExecutionStep.Complete, Outcome: { } outcome }) next = next.SetItem(id, outcome);
        }
        // Commit together: contradictory evidence cannot partially advance either
        // the whole queue or its separately captured optional-payment subset.
        outcomes = next; revision = state.Revision;
        Progress = Summary(captured, unknown);
        DeclineProgress = decline == null ? null : Summary(decline, unknown);
        return true;
    }

    private FiniteResolutionProgress Summary(ImmutableHashSet<string> ids, IEnumerable<string> unknown)
    {
        var completed = ids.Where(outcomes.ContainsKey).Select(id => outcomes[id]).ToArray();
        return new(ids.Count, completed.Length, ids.Count - completed.Length, unknown.Count(ids.Contains),
            completed.GroupBy(value => value).ToImmutableDictionary(group => group.Key, group => group.Count()));
    }
}
