using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Contracts;

namespace Mana.AI;

public enum AiDecisionOutcome { Offered, Submitted, Unavailable, Invalid, Cancelled, BudgetExceeded, Accepted, Rejected, ExecutionUnknown }
public sealed record AiDecisionRecord
{
    public AiDecisionStamp Stamp { get; init; }
    public AiTask Task { get; init; }
    public string PolicyId { get; init; }
    public AiDecisionOutcome Outcome { get; init; }
    public string ReasonCode { get; init; }
    public long Seed { get; init; }
    public int RemainingWork { get; init; }
    public TimeSpan Elapsed { get; init; }
    public AiTurnRoute? Route { get; init; }
    public string EpisodeId { get; init; } = "";
    public AiTurnPlan? RetainedPlan { get; init; }
    public long PlanVersion { get; init; }
    public AiPlanTransition? ProposedPlanTransition { get; init; }
    public bool PlanTransitionApplied { get; init; }
    // Historical commands are immutable evidence, never executable authority.
    // Persist these same facts for live policy requests and offline comparisons.
    public string Command { get; init; }
    private readonly JsonElement? answerEvidence;
    public JsonElement? AnswerEvidence { get => answerEvidence; init => answerEvidence = value?.Clone(); }

    [JsonConstructor]
    public AiDecisionRecord(AiDecisionStamp stamp, AiTask task, string policyId, AiDecisionOutcome outcome,
        string command, JsonElement? answerEvidence, string reasonCode, long seed, int remainingWork, TimeSpan elapsed)
        => (Stamp, Task, PolicyId, Outcome, Command, AnswerEvidence, ReasonCode, Seed, RemainingWork, Elapsed)
            = (stamp, task, policyId, outcome, command, answerEvidence, reasonCode, seed, remainingWork, elapsed);

    public AiDecisionRecord(AiDecisionStamp stamp, AiTask task, string policyId, AiDecisionOutcome outcome,
        DecisionCommand? answer, string reasonCode, long seed, int remainingWork, TimeSpan elapsed)
        : this(stamp, task, policyId, outcome, answer?.GetType().Name ?? "",
            answer == null ? null : JsonSerializer.SerializeToElement(answer, answer.GetType()), reasonCode, seed, remainingWork, elapsed) { }
}

// Retained event facts contain no card faces, prose, names or action handles.
// A proposal/accepted command is never promoted to damage or a resolved effect.
public sealed record AiObservedEvent(long Id, int Turn, string Kind, int? ActorId, string? CardId,
    PublicCardChange? Change, AiObservedImpact? Impact, int? CauseControllerId, string? CauseId)
{
    public ExecutionStep? ExecutionStep { get; init; }
    public string ExecutionId { get; init; } = "";
}
public sealed record AiObservedImpact(string Kind, long Amount, int? PlayerId, string? CardId,
    string? SourceCardId, int? Before, int? After, bool? Combat,
    int? SourceControllerId = null, int? RecipientControllerId = null, string? FromZone = null, string? ToZone = null);
public sealed record AiTurnRoute(int Turn, string SourceCardId, string StableRouteId);
public sealed record AiSeatHistory(ImmutableArray<AiDecisionRecord> Decisions, ImmutableArray<AiObservedEvent> Events)
{
    // Committed damage and resolving battlefield departures have their own
    // bounded lifetime. Draw/counter announcements cannot evict social evidence.
    // Null identifies an older private replay whose only history is Events.
    public ImmutableArray<AiObservedEvent>? ImpactEvents { get; init; }
    public long RestorationVersion { get; init; }
    public long SocialBaselineEventId { get; init; } = -1;
    public ImmutableHashSet<string> ReversedExecutionIds { get; init; } = [];

    // Retry guards have a different lifetime from the diagnostic ring. Evicting
    // a log entry cannot authorize repeating a cancelled/accepted activation.
    public ImmutableHashSet<AiTurnRoute> AttemptedRoutes { get; init; } = [];
    public bool RouteLimitReached { get; init; }

    public bool HasAttemptedRoute(int turn, string sourceCardId, string stableRouteId)
        // StableId belongs to the provider's rules occurrence and equivalent
        // route (Forge ActionSemantics.stableId includes host ID and timestamp).
        // A cancelled cast can renew its disclosed SourceCardId without renewing
        // that route. Missing stable identity retains the exact-source fallback.
        => stableRouteId.Length > 0
            ? AttemptedRoutes.Any(route => route.Turn == turn && route.StableRouteId == stableRouteId)
            : AttemptedRoutes.Contains(new(turn, sourceCardId, stableRouteId));
}

internal sealed class AiHistoryBuffer(int capacity)
{
    private readonly Queue<AiDecisionRecord> decisions = [];
    private readonly Queue<AiObservedEvent> events = [];
    private readonly AiImpactHistory impacts = new(capacity);
    private readonly HashSet<AiTurnRoute> routes = [];
    private int turn = -1;
    internal bool RouteLimitReached { get; private set; }
    private long lastEvent = -1;
    private long restorationVersion, socialBaseline = -1;
    private readonly HashSet<string> reversedExecutions = new(StringComparer.Ordinal);
    internal AiSeatHistory Snapshot => new(decisions.ToImmutableArray(), events.ToImmutableArray()) {
        ImpactEvents = impacts.Snapshot, AttemptedRoutes = routes.ToImmutableHashSet(), RouteLimitReached = RouteLimitReached,
        RestorationVersion = restorationVersion, SocialBaselineEventId = socialBaseline,
        ReversedExecutionIds = reversedExecutions.ToImmutableHashSet(StringComparer.Ordinal) };

    internal void Restore(GameSnapshot state)
    {
        if (state.Undo is not { LastRestoration: { } restored } undo || undo.RestorationVersion <= restorationVersion) return;
        var root = ExecutionFrames.Find(state, restored.ExecutionId);
        if (undo.RestorationVersion != restorationVersion + 1 || root == null) {
            // Missing intermediate ancestry cannot justify an invented inverse.
            // Keep the evidence; rebase only its derived social contribution.
            socialBaseline = Math.Max(socialBaseline, lastEvent);
        } else {
            reversedExecutions.Add(restored.ExecutionId);
            foreach (var occurrence in state.Executions!.Occurrences) {
                var current = occurrence;
                while (current.ParentId is { } parent) {
                    if (parent == restored.ExecutionId) { reversedExecutions.Add(occurrence.Id); break; }
                    current = ExecutionFrames.Find(state, parent)!;
                }
            }
        }
        restorationVersion = undo.RestorationVersion;
        var retained = events.Concat(impacts.Snapshot).Select(entry => entry.ExecutionId).ToHashSet(StringComparer.Ordinal);
        reversedExecutions.RemoveWhere(id => !retained.Contains(id));
    }
    internal void ObserveTurn(int current)
    {
        ValidateTurn(current);
        if (current == turn) return;
        turn = current; routes.Clear(); RouteLimitReached = false;
    }
    internal void ValidateTurn(int current)
    {
        if (current < turn) throw new ArgumentException("An AI turn cannot move backward.", nameof(current));
    }
    internal void Record(AiDecisionRecord record)
    {
        if (record.Outcome is AiDecisionOutcome.Accepted or AiDecisionOutcome.Rejected && record.Route is { } route && route.Turn == turn
                && !routes.Contains(route)) {
            if (routes.Count == 4096) RouteLimitReached = true;
            else routes.Add(route);
        }
        decisions.Enqueue(record);
        while (decisions.Count > capacity) decisions.Dequeue();
    }
    internal void Observe(ImmutableArray<ActivityEntry> activity)
    {
        if (activity.IsDefaultOrEmpty) return;
        foreach (var entry in activity.OrderBy(entry => entry.Id)) {
            if (entry.Id <= lastEvent) continue;
            lastEvent = entry.Id;
            var impact = entry.Impact;
            var observed = new AiObservedEvent(entry.Id, entry.Turn, entry.Kind, entry.PlayerId, entry.CardId, entry.Change,
                impact == null ? null : new(impact.Kind, impact.Amount, impact.PlayerId, impact.CardId,
                    impact.SourceCardId, impact.Before, impact.After, impact.Combat, impact.SourceControllerId,
                    impact.RecipientControllerId, impact.FromZone, impact.ToZone), entry.Cause?.ControllerId, entry.Cause?.Id) {
                ExecutionStep = entry.ExecutionStep, ExecutionId = entry.ExecutionId };
            impacts.Observe(observed);
            events.Enqueue(observed);
            while (events.Count > capacity) events.Dequeue();
        }
    }
}
