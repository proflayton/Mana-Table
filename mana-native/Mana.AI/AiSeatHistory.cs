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
// Private bounded evidence for reconsidering one route, never command authority.
// Missing evidence in an older replay retains the conservative retry guard.
public sealed record AiRouteProgress(AiTurnRoute Route, AiDecisionStamp Submission,
    long ExecutionAnchor, long ActivityAnchor, long RestorationVersion, ImmutableArray<ActionEffectKind> Effects)
{
    public AiExecutionReceipt? Receipt { get; init; }
    public ExecutionOccurrence? Execution { get; init; }
    public long PreparationSequence { get; init; } = -1;
    public AiObservedEvent? Cost { get; init; }
    public AiObservedEvent? Effect { get; init; }
    public AiObservedEvent? Untap { get; init; }
    public bool Closed { get; init; }

    internal static bool Supported(ActionSemantics action) => action is { StableId.Length: > 0,
        Repeatable: true, ManaAbility: false, Affordability: ActionAffordability.Payable }
        && action.Costs is [{ Kind: ActionCostKind.Tap, Amount: 1, FromSource: true }]
        && EffectsSupported(action.Effects);
    private static bool EffectsSupported(ImmutableArray<ActionEffectKind> effects) => !effects.IsDefaultOrEmpty
        && effects.All(effect => Enum.IsDefined(effect))
        && effects.Any(effect => effect is ActionEffectKind.Damage or ActionEffectKind.LifeGain or ActionEffectKind.Draw);
    internal bool Productive(AiObservedEvent entry) => entry.ExecutionId == Execution?.Id
        && entry.ExecutionStep == ExecutionStep.Resolution && entry.Turn == Route.Turn && entry.Id > ActivityAnchor
        && (Effects.Contains(ActionEffectKind.Damage)
                && entry.Impact is { Kind: "damage", Amount: > 0 } damage
                && damage.SourceCardId == Route.SourceCardId && damage.SourceControllerId == Submission.SeatId
            || Effects.Contains(ActionEffectKind.LifeGain)
                && entry.Impact is { Kind: "life", Amount: > 0, Before: { } before, After: { } after } life
                && life.PlayerId == Submission.SeatId && after > before && life.Amount == (long)after - before
            || Effects.Contains(ActionEffectKind.Draw) && entry.Kind == "draw" && entry.ActorId == Submission.SeatId);
    internal bool Tap(AiObservedEvent entry, bool tapped) => entry.Turn == Route.Turn && entry.CardId == Route.SourceCardId
        && entry.Kind == "tapped" && entry.Change is { Kind: "state", Property: "tapped" } change
        && change.Before == (tapped ? 0 : 1) && change.After == (tapped ? 1 : 0)
        && (!tapped || entry.ExecutionId == Execution?.Id && entry.ExecutionStep == ExecutionStep.Cost
            && entry.ActorId == Submission.SeatId);
    internal bool Complete => !Closed && Receipt == AiExecutionReceipt.Accepted
        && Execution is { Step: ExecutionStep.Complete, Outcome: ExecutionOutcome.Resolved }
        && Cost != null && Effect != null && Untap != null;
    internal bool CanRenew(GameSnapshot state, string sourceCardId, ActionSemantics action)
        => Complete && state.Executions != null && Supported(action) && action.TargetsAvailable != false && Route.Turn == state.Turn
            && Route.StableRouteId == action.StableId && Route.SourceCardId == sourceCardId && Effects.SequenceEqual(action.Effects)
            && RestorationVersion == (state.Undo?.RestorationVersion ?? 0)
            && state.Viewer?.Zone("Battlefield").Cards.Any(card => card.VisualId == sourceCardId
                && !card.Tapped && !card.PhasedOut && !card.FaceDown) == true;

    internal static string Key(AiTurnRoute route) => route.StableRouteId.Length > 0
        ? "route:" + route.StableRouteId : "source:" + route.SourceCardId;

    internal static void ValidateRecorded(ImmutableArray<AiRouteProgress> progress, AiSeatHistory history, GameSnapshot state)
    {
        long lastEvent = state.Activity.Select(entry => entry.Id).Concat(history.Events.Select(entry => entry.Id))
            .Concat(history.ImpactEvents?.Select(entry => entry.Id) ?? []).DefaultIfEmpty(-1).Max();
        if (progress.IsDefault || progress.Length > 4096
            || progress.Any(item => item == null || item.Route == null
                || !UndoFacts.Identifier(item.Route.SourceCardId) || !UndoFacts.Identifier(item.Route.StableRouteId))
            || progress.Select(item => Key(item.Route)).Distinct(StringComparer.Ordinal).Count() != progress.Length) throw Invalid();
        foreach (var item in progress) {
            if (item.Route == null || !UndoFacts.Identifier(item.Route.SourceCardId)
                || !UndoFacts.Identifier(item.Route.StableRouteId) || item.Route.Turn != state.Turn
                || item.Submission.GameId != state.Id || item.Submission.SeatId != state.ViewerId
                || item.Submission.Revision < 0 || item.Submission.Revision > state.Revision
                || item.Submission.BoardRevision < 0 || item.Submission.BoardRevision > state.BoardRevision
                || !UndoFacts.Identifier(item.Submission.DecisionId) || item.Submission.OfferVersion < 0
                || item.Submission.RestorationVersion != item.RestorationVersion
                || item.ExecutionAnchor < 0 || item.ActivityAnchor < -1 || item.RestorationVersion < 0
                || state.Executions == null || item.ExecutionAnchor > state.Executions.LastSequence
                || item.PreparationSequence > state.Executions.LastSequence || item.ActivityAnchor > lastEvent
                || item.RestorationVersion > history.RestorationVersion || !EffectsSupported(item.Effects)
                || item.Receipt.HasValue && !Enum.IsDefined(item.Receipt.Value)
                || !item.Closed && item.RestorationVersion != history.RestorationVersion) throw Invalid();
            if (item.Execution is { } root) {
                if (root.Kind != ExecutionKind.Activation || root.ParentId != null || root.BudgetRootId != root.Id
                    || root.RuleKind != null || root.RuleResult != null
                    || !UndoFacts.Identifier(root.Id) || root.ActorPlayerId != state.ViewerId
                    || root.SourceCardId != item.Route.SourceCardId || root.StableRouteId != item.Route.StableRouteId
                    || item.PreparationSequence <= item.ExecutionAnchor || !Enum.IsDefined(root.Step)
                    || root.Outcome.HasValue && !Enum.IsDefined(root.Outcome.Value)
                    || (root.Step == ExecutionStep.Complete) != root.Outcome.HasValue) throw Invalid();
            } else if (item.PreparationSequence != -1 || item.Cost != null || item.Effect != null || item.Untap != null) throw Invalid();
            if (item.Receipt == AiExecutionReceipt.Accepted
                    && !history.AttemptedRoutes.Any(route => route.Turn == item.Route.Turn && Key(route) == Key(item.Route))
                || item.Cost is { } cost && (cost.Id <= item.ActivityAnchor || !item.Tap(cost, true))
                || item.Effect is { } effect && (item.Cost == null || effect.Id <= item.Cost.Id || !item.Productive(effect))
                || item.Untap is { } untap && (item.Effect == null || untap.Id <= item.Effect.Id || !item.Tap(untap, false))) throw Invalid();
            foreach (var entry in new[] { item.Cost, item.Effect, item.Untap }) {
                if (entry == null) continue;
                if (entry.Id < 0 || entry.Id > lastEvent || entry.ExecutionId == null
                    || entry.ExecutionId.Length > 0 && !UndoFacts.Identifier(entry.ExecutionId)
                    || entry.ExecutionStep.HasValue && !Enum.IsDefined(entry.ExecutionStep.Value)
                    || entry.ExecutionId.Length == 0 && entry.ExecutionStep != null
                    || entry.CauseId != null && entry.CauseId.Length > 0 && !UndoFacts.Identifier(entry.CauseId)
                    || entry.ActorId.HasValue && !state.Players.Any(player => player.Id == entry.ActorId)
                    || entry.Impact?.PlayerId is { } playerId && !state.Players.Any(player => player.Id == playerId)) throw Invalid();
            }
        }
        static ArgumentException Invalid() => new("Recorded route progress requires bounded, causal, restoration-aware execution facts.");
    }
}
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
    public ImmutableArray<AiRouteProgress> RouteProgress { get; init; } = [];
    public bool RouteLimitReached { get; init; }

    public bool HasAttemptedRoute(int turn, string sourceCardId, string stableRouteId)
        // StableId belongs to the provider's rules occurrence and equivalent
        // route (Forge ActionSemantics.stableId includes host ID and timestamp).
        // A cancelled cast can renew its disclosed SourceCardId without renewing
        // that route. Missing stable identity retains the exact-source fallback.
        => stableRouteId.Length > 0
            ? AttemptedRoutes.Any(route => route.Turn == turn && route.StableRouteId == stableRouteId)
            : AttemptedRoutes.Any(route => route.Turn == turn && route.SourceCardId == sourceCardId && route.StableRouteId.Length == 0);

    public bool HasAttemptedRoute(GameSnapshot state, string sourceCardId, ActionSemantics action)
        => HasAttemptedRoute(state.Turn, sourceCardId, action.StableId)
            && !RouteProgress.Any(progress => progress.CanRenew(state, sourceCardId, action));
}

internal sealed class AiHistoryBuffer(int capacity)
{
    private readonly Queue<AiDecisionRecord> decisions = [];
    private readonly Queue<AiObservedEvent> events = [];
    private readonly AiImpactHistory impacts = new(capacity);
    private readonly Dictionary<string, AiTurnRoute> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiRouteProgress> routeProgress = new(StringComparer.Ordinal);
    private int turn = -1;
    internal bool RouteLimitReached { get; private set; }
    private long lastEvent = -1;
    private long restorationVersion, socialBaseline = -1;
    private readonly HashSet<string> reversedExecutions = new(StringComparer.Ordinal);
    internal AiSeatHistory Snapshot => new(decisions.ToImmutableArray(), events.ToImmutableArray()) {
        ImpactEvents = impacts.Snapshot, AttemptedRoutes = routes.Values.ToImmutableHashSet(), RouteProgress = routeProgress.Values.ToImmutableArray(), RouteLimitReached = RouteLimitReached,
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
        foreach (var key in routeProgress.Keys.ToArray()) routeProgress[key] = routeProgress[key] with { Closed = true };
        var retained = events.Concat(impacts.Snapshot).Select(entry => entry.ExecutionId).ToHashSet(StringComparer.Ordinal);
        reversedExecutions.RemoveWhere(id => !retained.Contains(id));
    }
    internal void ObserveTurn(int current)
    {
        ValidateTurn(current);
        if (current == turn) return;
        turn = current; routes.Clear(); routeProgress.Clear(); RouteLimitReached = false;
    }
    internal void ValidateTurn(int current)
    {
        if (current < turn) throw new ArgumentException("An AI turn cannot move backward.", nameof(current));
    }
    internal void Record(AiDecisionRecord record)
    {
        if (record.Outcome is AiDecisionOutcome.Accepted or AiDecisionOutcome.Rejected or AiDecisionOutcome.ExecutionUnknown
                && record.Route is { } route && route.Turn == turn && !routes.ContainsKey(AiRouteProgress.Key(route))) {
            if (routes.Count == 4096) RouteLimitReached = true;
            else routes.Add(AiRouteProgress.Key(route), route);
        }
        decisions.Enqueue(record);
        while (decisions.Count > capacity) decisions.Dequeue();
    }
    internal void BeginRoute(AiTurnRoute route, AiDecisionFrame frame, ActionSemantics action)
    {
        foreach (var pending in routeProgress.Where(pair => pair.Value.Execution == null && !pair.Value.Closed).Select(pair => pair.Key).ToArray())
            routeProgress[pending] = routeProgress[pending] with { Closed = true };
        string key = AiRouteProgress.Key(route);
        // A renewal is consumed at handoff, even if the next command fails.
        routeProgress.Remove(key);
        if (routes.Count == 4096 && !routes.ContainsKey(key)) { RouteLimitReached = true; return; }
        if (AiRouteProgress.Supported(action) && frame.State.Executions is { } executions)
            routeProgress[key] = new(route, frame.Stamp, executions.LastSequence,
                Math.Max(lastEvent, frame.State.Activity.Select(entry => entry.Id).DefaultIfEmpty(-1).Max()),
                restorationVersion, action.Effects);
    }
    internal void Receipt(AiTurnRoute? route, AiExecutionReceipt receipt)
    {
        if (route == null || !routeProgress.TryGetValue(AiRouteProgress.Key(route), out var progress)) return;
        routeProgress[AiRouteProgress.Key(route)] = progress with { Receipt = receipt,
            Closed = progress.Closed || receipt != AiExecutionReceipt.Accepted };
    }
    internal void ObserveProgress(GameSnapshot state)
    {
        if (routeProgress.Values.All(progress => progress.Closed)) return;
        var frame = state.Executions;
        var preparation = frame?.Transitions.Where(transition => transition.Step == ExecutionStep.Preparation)
            .GroupBy(transition => transition.ExecutionId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Sequence, StringComparer.Ordinal);
        var roots = frame?.Occurrences.Where(root => root.Kind == ExecutionKind.Activation && root.ParentId == null
                && root.BudgetRootId == root.Id && root.StableRouteId != null)
            .GroupBy(root => root.StableRouteId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var occurrences = frame?.Occurrences.ToDictionary(root => root.Id, StringComparer.Ordinal);
        var activity = state.Activity.OrderBy(entry => entry.Id).Select(Observed).ToArray();
        foreach (var key in routeProgress.Keys.ToArray()) {
            var progress = routeProgress[key];
            if (progress.Closed) continue;
            if (frame == null || progress.RestorationVersion != (state.Undo?.RestorationVersion ?? 0)) { Close(); continue; }
            var retained = progress.Execution == null ? null : occurrences!.GetValueOrDefault(progress.Execution.Id);
            if (retained != null && (retained.Kind != ExecutionKind.Activation || retained.ParentId != null
                || retained.BudgetRootId != retained.Id || retained.StableRouteId != progress.Route.StableRouteId
                || retained.SourceCardId != progress.Route.SourceCardId || retained.ActorPlayerId != progress.Submission.SeatId
                || progress.Execution!.Step == ExecutionStep.Complete
                    && (retained.Step != ExecutionStep.Complete || retained.Outcome != progress.Execution.Outcome))) { Close(); continue; }
            if (progress.Complete) continue;
            var candidates = (roots!.GetValueOrDefault(progress.Route.StableRouteId) ?? [])
                .Where(root => root.ActorPlayerId == progress.Submission.SeatId && root.SourceCardId == progress.Route.SourceCardId
                    && preparation!.TryGetValue(root.Id, out var sequence) && sequence > progress.ExecutionAnchor).ToArray();
            if (candidates.Length > 1 || progress.Execution != null && candidates.Any(root => root.Id != progress.Execution.Id)) { Close(); continue; }
            if (progress.Execution == null) {
                if (frame.FirstSequence > progress.ExecutionAnchor + 1) { Close(); continue; }
                if (candidates.Length == 1) progress = progress with { Execution = candidates[0], PreparationSequence = preparation![candidates[0].Id] };
                else if (state.Decision?.Payload is PriorityPayload && state.Revision > progress.Submission.Revision) { Close(); continue; }
            } else {
                var current = retained;
                if (current == null && progress.Execution.Step != ExecutionStep.Complete) { Close(); continue; }
                if (current != null) {
                    progress = progress with { Execution = current };
                }
            }
            if (progress.Execution is { Step: ExecutionStep.Complete, Outcome: not ExecutionOutcome.Resolved }) { Close(); continue; }
            foreach (var observed in activity) {
                var entry = observed;
                if (entry.Id <= progress.ActivityAnchor) continue;
                if (progress.Cost == null && progress.Tap(observed, true)) progress = progress with { Cost = observed };
                if (progress.Cost != null && progress.Effect == null && entry.Id > progress.Cost.Id && progress.Productive(observed))
                    progress = progress with { Effect = observed };
                if (progress.Effect != null && progress.Untap == null && entry.Id > progress.Effect.Id && progress.Tap(observed, false))
                    progress = progress with { Untap = observed };
            }
            // A truncated tail cannot fill a missing cost/effect with later,
            // unrelated evidence. Sealed facts survive diagnostic eviction.
            long needed = progress.Cost?.Id ?? progress.ActivityAnchor;
            if (progress.Effect != null) needed = progress.Effect.Id;
            if (activity.Length > 0 && needed >= 0 && activity[0].Id > needed + 1
                && (progress.Cost == null || progress.Effect == null || progress.Untap == null)) { Close(); continue; }
            routeProgress[key] = progress;
            void Close() => routeProgress[key] = progress with { Closed = true };
        }
    }
    private static AiObservedEvent Observed(ActivityEntry entry)
    {
        var impact = entry.Impact;
        return new(entry.Id, entry.Turn, entry.Kind, entry.PlayerId, entry.CardId, entry.Change,
            impact == null ? null : new(impact.Kind, impact.Amount, impact.PlayerId, impact.CardId,
                impact.SourceCardId, impact.Before, impact.After, impact.Combat, impact.SourceControllerId,
                impact.RecipientControllerId, impact.FromZone, impact.ToZone), entry.Cause?.ControllerId, entry.Cause?.Id) {
            ExecutionStep = entry.ExecutionStep, ExecutionId = entry.ExecutionId };
    }
    internal void Observe(ImmutableArray<ActivityEntry> activity)
    {
        if (activity.IsDefaultOrEmpty) return;
        foreach (var entry in activity.OrderBy(entry => entry.Id)) {
            if (entry.Id <= lastEvent) continue;
            lastEvent = entry.Id;
            var observed = Observed(entry);
            impacts.Observe(observed);
            events.Enqueue(observed);
            while (events.Count > capacity) events.Dequeue();
        }
    }
}
