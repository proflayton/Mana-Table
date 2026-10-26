using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Contracts;

namespace Mana.AI;

public enum AiDecisionOutcome { Offered, Submitted, Unavailable, Invalid, Cancelled, BudgetExceeded, Accepted, Rejected, ExecutionUnknown }
public sealed record AiDecisionRecord(AiDecisionStamp Stamp, AiTask Task, string PolicyId,
    AiDecisionOutcome Outcome, [property: JsonIgnore] DecisionCommand? Answer, string ReasonCode, long Seed, int RemainingWork, TimeSpan Elapsed)
{
    public AiTurnRoute? Route { get; init; }
    public string EpisodeId { get; init; } = "";
    public string Command => Answer?.GetType().Name ?? "";
    // Preserve concrete payload fields in diagnostic JSON without introducing
    // a second executable command decoder or changing the provider wire format.
    public JsonElement? AnswerEvidence => Answer == null ? null : JsonSerializer.SerializeToElement(Answer, Answer.GetType());
}

// Retained event facts contain no card faces, prose, names or action handles.
// A proposal/accepted command is never promoted to damage or a resolved effect.
public sealed record AiObservedEvent(long Id, int Turn, string Kind, int? ActorId, string? CardId,
    PublicCardChange? Change, AiObservedImpact? Impact, int? CauseControllerId, string? CauseId);
public sealed record AiObservedImpact(string Kind, long Amount, int? PlayerId, string? CardId,
    string? SourceCardId, int? Before, int? After, bool? Combat);
public sealed record AiTurnRoute(int Turn, string SourceCardId, string StableRouteId);
public sealed record AiSeatHistory(ImmutableArray<AiDecisionRecord> Decisions, ImmutableArray<AiObservedEvent> Events)
{
    // Retry guards have a different lifetime from the diagnostic ring. Evicting
    // a log entry cannot authorize repeating a cancelled/accepted activation.
    public ImmutableHashSet<AiTurnRoute> AttemptedRoutes { get; init; } = [];
    public bool RouteLimitReached { get; init; }
}

internal sealed class AiHistoryBuffer(int capacity)
{
    private readonly Queue<AiDecisionRecord> decisions = [];
    private readonly Queue<AiObservedEvent> events = [];
    private readonly HashSet<AiTurnRoute> routes = [];
    private int turn = -1;
    internal bool RouteLimitReached { get; private set; }
    private long lastEvent = -1;
    internal AiSeatHistory Snapshot => new(decisions.ToImmutableArray(), events.ToImmutableArray()) {
        AttemptedRoutes = routes.ToImmutableHashSet(), RouteLimitReached = RouteLimitReached };
    internal void ObserveTurn(int current)
    {
        if (current < turn) throw new ArgumentException("An AI turn cannot move backward.", nameof(current));
        if (current == turn) return;
        turn = current; routes.Clear(); RouteLimitReached = false;
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
            events.Enqueue(new(entry.Id, entry.Turn, entry.Kind, entry.PlayerId, entry.CardId, entry.Change,
                impact == null ? null : new(impact.Kind, impact.Amount, impact.PlayerId, impact.CardId,
                    impact.SourceCardId, impact.Before, impact.After, impact.Combat), entry.Cause?.ControllerId, entry.Cause?.Id));
            while (events.Count > capacity) events.Dequeue();
        }
    }
}
