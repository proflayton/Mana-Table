using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public sealed record FiniteResolutionProgress(int CapturedCount, int CompletedCount, int RemainingCount,
    int UnknownCount, ImmutableDictionary<ExecutionOutcome, int> Outcomes)
{
    public int ResolvedCount => Outcomes.GetValueOrDefault(ExecutionOutcome.Resolved);
    public int DeclinedCount => Outcomes.GetValueOrDefault(ExecutionOutcome.Declined);
    public int CancelledCount => Outcomes.GetValueOrDefault(ExecutionOutcome.Cancelled);
    public int CounteredCount => Outcomes.GetValueOrDefault(ExecutionOutcome.Countered);
    public int FailedCount => Outcomes.GetValueOrDefault(ExecutionOutcome.Failed);
}

/// <summary>Finite consent for disclosed occurrences, with a separate optional
/// negative-payment policy. Every answer still uses the current ordinary offer.</summary>
public sealed class FiniteResolutionIntent : IFiniteDecisionPolicy
{
    private sealed record Member(string ExecutionId, string RootId, string? ParentId, ExecutionKind Kind,
        int? ActorId, string? SourceId);
    private sealed record Queued(int Position, string ExecutionId, StackActionKind Kind, int? ControllerId,
        StackRepetition? Repetition, ImmutableArray<(string Kind, string Id, int? Player)> Targets);
    private readonly object gate = new();
    private readonly string game, phase;
    private readonly int viewer, turn;
    private readonly int? activePlayer;
    private readonly long actionVersion, choiceVersion, initialInterruption, captureRevision;
    private readonly OptionalDeclineOffer? decline;
    private readonly ImmutableDictionary<string, Member> members;
    private readonly ImmutableDictionary<string, Queued> originalQueue;
    private readonly ImmutableHashSet<string> selectedStack;
    private readonly ImmutableHashSet<string> declineMembers;
    private readonly Dictionary<string, ExecutionOutcome> outcomes = new(StringComparer.Ordinal);
    private readonly HashSet<string> unknown = new(StringComparer.Ordinal), answered = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenOffers = new(StringComparer.Ordinal);
    private HashSet<string> remainingQueue;
    private long revision;
    private bool active = true, paused;
    private string? submittedScope, submittedPriority;
    private string stopReason = "";
    private string lastOfferId = "", lastOfferExecution = "";
    private string serverGrantId = "";
    private ImmutableArray<string> serverMembers = [];
    private bool revokeRequested;

    private FiniteResolutionIntent(GameSnapshot state, ImmutableArray<StackItem> selected, OptionalDeclineOffer? offer)
    {
        game = state.Id; viewer = state.ViewerId; turn = state.Turn; phase = state.PhaseKey; activePlayer = state.ActivePlayerId;
        actionVersion = state.StackActionVersion!.Value; choiceVersion = state.ResolutionChoiceVersion!.Value;
        initialInterruption = state.StackInterruptionVersion!.Value; captureRevision = revision = state.Revision; decline = offer;
        if (offer != null) { lastOfferId = offer.OfferId; lastOfferExecution = offer.ProducingTriggerExecutionId; seenOffers.Add(offer.OfferId); }
        originalQueue = state.Stack.Select((item, position) => (item, position)).ToImmutableDictionary(pair => pair.item.Id,
            pair => new Queued(pair.position, pair.item.ExecutionId, pair.item.Kind, pair.item.ControllerId,
                pair.item.Repetition, pair.item.Targets.Select(Target).ToImmutableArray()), StringComparer.Ordinal);
        remainingQueue = originalQueue.Keys.ToHashSet(StringComparer.Ordinal);
        selectedStack = selected.Select(item => item.Id).ToImmutableHashSet(StringComparer.Ordinal);
        CapturedStackIds = selected.Select(item => item.Id).ToImmutableArray();
        CapturedExecutionIds = selected.Select(item => item.ExecutionId)
            .Concat(offer == null ? [] : new[] { offer.ProducingTriggerExecutionId }).Distinct(StringComparer.Ordinal).ToImmutableArray();
        members = CapturedExecutionIds.ToImmutableDictionary(id => id, id => {
            var occurrence = ExecutionFrames.Find(state, id)!;
            return new Member(id, occurrence.BudgetRootId, occurrence.ParentId, occurrence.Kind, occurrence.ActorPlayerId, occurrence.SourceCardId);
        }, StringComparer.Ordinal);
        declineMembers = offer == null ? ImmutableHashSet<string>.Empty
            : selected.Where(item => item.Repetition is { } repeat && repeat.GroupKey == offer.GroupKey
                    && repeat.SourceCardId == offer.SourceCardId).Select(item => item.ExecutionId)
                .Append(offer.ProducingTriggerExecutionId).ToImmutableHashSet(StringComparer.Ordinal);
        SourceCardId = offer?.SourceCardId ?? selected.FirstOrDefault()?.Repetition?.SourceCardId
            ?? members.Values.First().SourceId ?? "";
        SourceName = offer == null ? selected[0].Name : ExecutionFrames.Find(state, offer.ProducingTriggerExecutionId)?.SourceName ?? "Ability";
    }

    public static FiniteResolutionIntent? TryArm(GameSnapshot state, IEnumerable<string> exactStackIds)
        => TryArm(state, exactStackIds, null);

    public static FiniteResolutionIntent? TryArm(GameSnapshot state, IEnumerable<string> exactStackIds, OptionalDeclineOffer? optionalDecline)
        => TryArm(state, exactStackIds, optionalDecline, wholeQueue: false);

    // Explicitly combines two consents: yielding the finite current queue and
    // declining only its already captured matching optional trigger payments.
    public static FiniteResolutionIntent? TryArmQueueWithDecline(GameSnapshot state, IEnumerable<string> exactCurrentQueueIds,
        OptionalDeclineOffer optionalDecline)
    {
        ArgumentNullException.ThrowIfNull(optionalDecline);
        return TryArm(state, exactCurrentQueueIds, optionalDecline, wholeQueue: true);
    }

    private static FiniteResolutionIntent? TryArm(GameSnapshot state, IEnumerable<string> exactStackIds,
        OptionalDeclineOffer? optionalDecline, bool wholeQueue)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(exactStackIds);
        var ids = exactStackIds.ToImmutableArray();
        if (state.Status != "playing" || state.Viewer is not { Eliminated: false } || state.Revision < 0
            || state.StackActionVersion is not >= 0 || state.ResolutionChoiceVersion is not >= 0
            || state.StackInterruptionVersion is not >= 0 || state.Executions == null || state.Stack.IsDefault
            || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || state.Stack.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || item.Targets.IsDefault || item.Targets.Any(target => target == null))
            || state.Stack.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Stack.Length) return null;
        try { ExecutionFrames.Validate(state); FiniteResolutionFacts.Validate(state); FiniteStackYieldFacts.Validate(state); }
        catch (ArgumentException) { return null; }
        if (state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active }) return null;
        var selected = state.Stack.Where(item => ids.Contains(item.Id)).ToImmutableArray();
        if (selected.Length != ids.Length || selected.Any(item => item.ExecutionId.Length == 0
                || ExecutionFrames.Find(state, item.ExecutionId) is not { Step: not ExecutionStep.Complete })
            || selected.Select(item => item.ExecutionId).Distinct(StringComparer.Ordinal).Count() != selected.Length) return null;
        if (optionalDecline == null) {
            if (!OwnPriority(state) || selected.IsEmpty || !ids.Contains(state.Stack[0].Id)) return null;
        } else if (!OwnedDecline(state, optionalDecline)
            || (wholeQueue ? selected.Length != state.Stack.Length : selected.Any(item => item.Kind != StackActionKind.TriggeredAbility
                || item.Repetition is not { } repeat || repeat.GroupKey != optionalDecline.GroupKey
                || repeat.SourceCardId != optionalDecline.SourceCardId))) return null;
        return new(state, selected, optionalDecline);
    }

    public ImmutableArray<string> CapturedStackIds { get; }
    public ImmutableArray<string> CapturedExecutionIds { get; }
    public string SourceCardId { get; }
    public string SourceName { get; }
    public bool DeclinesOptionalPayments => decline != null;
    public bool IsActive { get { lock (gate) return active; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    public string StopReason { get { lock (gate) return stopReason; } }
    public FiniteResolutionProgress Progress { get { lock (gate) return ProgressFor(members.Keys); } }
    public FiniteResolutionProgress? DeclineProgress { get { lock (gate) return decline == null ? null : ProgressFor(declineMembers); } }
    // A separate owner-thread projection consumes only accepted publications.
    // Worker progress remains policy state, never presentation authority.
    public FiniteResolutionProgressProjection CaptureProgressProjection()
        => new(game, viewer, captureRevision, members.Values.Select(member => new FiniteResolutionProgressProjection.Member(
            member.ExecutionId, member.RootId, member.ParentId, member.Kind, member.ActorId, member.SourceId)).ToImmutableArray(),
            decline == null ? null : declineMembers);
    private FiniteResolutionProgress ProgressFor(IEnumerable<string> ids)
    {
        var captured = ids.ToArray();
        var completed = captured.Where(outcomes.ContainsKey).Select(id => outcomes[id]).ToArray();
        return new(captured.Length, completed.Length, captured.Length - completed.Length, captured.Count(unknown.Contains),
            completed.GroupBy(value => value).ToImmutableDictionary(group => group.Key, group => group.Count()));
    }
    public void Pause() { lock (gate) if (active) paused = true; }
    public void Resume() { lock (gate) if (active) paused = false; }
    public void Stop() { lock (gate) End("stopped"); }
    internal sealed record ServerRevocation(RevokeFiniteStackYield Command, int ViewerId, long MinimumRevision);
    internal RevokeFiniteStackYield? RequestServerRevocation() => CaptureServerRevocation()?.Command;
    internal ServerRevocation? CaptureServerRevocation()
    {
        lock (gate) {
            if (serverGrantId.Length == 0) return null;
            revokeRequested = true;
            return new(new(game, serverGrantId), viewer, revision);
        }
    }
    public bool OwnsServerGrant(FiniteStackYieldState grant)
    {
        lock (gate) return serverGrantId.Length > 0 && grant.GrantId == serverGrantId;
    }
    internal void AcknowledgeServerRevocation(GameSnapshot state, RevokeFiniteStackYield command)
    {
        lock (gate) {
            if (!revokeRequested || command.GameId != game || command.GrantId != serverGrantId
                || state.Id != game || state.ViewerId != viewer) return;
            // Only the serial revoke/observe boundary calls this. An ordinary
            // old Idle projection cannot erase a grant proposed just before an
            // in-flight send. A validated revoke ACK can also confirm that a
            // proposed grant cancelled before dispatch never became active.
            if (state.FiniteStackYield is { Status: FiniteStackYieldStatus.Idle }
                || state.FiniteStackYield is { Status: FiniteStackYieldStatus.Revoked or FiniteStackYieldStatus.Completed } terminal
                    && terminal.GrantId == serverGrantId)
                ClearServerGrant();
        }
    }
    private void ClearServerGrant()
    {
        serverGrantId = ""; serverMembers = []; revokeRequested = false;
        submittedScope = submittedPriority = null;
    }

    // Presentation membership only. A common budget root does not establish
    // ancestry or authorize any command belonging to a later occurrence.
    public bool IncludesExecution(GameSnapshot state, string id)
    {
        if (state.Id != game || state.ViewerId != viewer || string.IsNullOrEmpty(id)) return false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(id)) {
            if (members.ContainsKey(id)) return true;
            if (ExecutionFrames.Find(state, id)?.ParentId is not { } parent) return false;
            id = parent;
        }
        return false;
    }

    public bool CanContinue(GameSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate) return Continue(state);
    }

    private bool Continue(GameSnapshot state)
    {
        if (!active) return false;
        if (state.Id != game || state.ViewerId != viewer || state.Turn != turn || state.PhaseKey != phase
            || state.ActivePlayerId != activePlayer || state.Status is not ("playing" or "resolving")
            || state.Viewer is not { Eliminated: false }) return End("scope-changed");
        if (state.Revision < revision) return true;
        try { ExecutionFrames.Validate(state); FiniteResolutionFacts.Validate(state); FiniteStackYieldFacts.Validate(state); }
        catch (ArgumentException) { return End("execution-evidence-invalid"); }
        foreach (var member in members.Values) {
            var actual = ExecutionFrames.Find(state, member.ExecutionId);
            if (actual == null) {
                if (!outcomes.ContainsKey(member.ExecutionId)) unknown.Add(member.ExecutionId);
                continue;
            }
            if (actual.BudgetRootId != member.RootId || actual.ParentId != member.ParentId || actual.Kind != member.Kind
                || actual.ActorPlayerId != member.ActorId || actual.SourceCardId != member.SourceId) return End("execution-owner-changed");
            if (actual is { Step: ExecutionStep.Complete, Outcome: { } outcome }) {
                if (outcomes.TryGetValue(member.ExecutionId, out var previous) && previous != outcome) return End("execution-outcome-changed");
                outcomes[member.ExecutionId] = outcome; unknown.Remove(member.ExecutionId);
            }
        }
        revision = state.Revision;
        if (unknown.Count > 0) return End("execution-outcome-unknown");
        if (state.StackActionVersion != actionVersion || state.ResolutionChoiceVersion != choiceVersion
            || state.StackInterruptionVersion is not { } total || total < initialInterruption) return End("interrupted");
        if (state.FiniteStackYield is { } server) {
            if (server.Status == FiniteStackYieldStatus.Active && server.GrantId != serverGrantId) return End("unowned-server-grant");
            if (serverGrantId.Length > 0 && server.GrantId == serverGrantId) {
                if (!server.ExecutionIds.ToHashSet(StringComparer.Ordinal).SetEquals(serverMembers)
                    || server.StackActionVersion != actionVersion || server.ResolutionChoiceVersion != choiceVersion)
                    return End("server-grant-changed");
                if (revokeRequested && server.Status is FiniteStackYieldStatus.Revoked or FiniteStackYieldStatus.Completed) {
                    ClearServerGrant();
                } else if (server.Status == FiniteStackYieldStatus.Revoked) return End("server-grant-revoked");
            }
        } else if (serverGrantId.Length > 0) return End("server-grant-unavailable");
        if (state.Stack.IsDefault) return End("queue-unavailable");
        int position = -1;
        var previousQueue = remainingQueue;
        var nextQueue = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Stack) {
            if (item == null || !previousQueue.Contains(item.Id) || !nextQueue.Add(item.Id)
                || !originalQueue.TryGetValue(item.Id, out var original)
                || original.ExecutionId != item.ExecutionId || original.Kind != item.Kind || original.ControllerId != item.ControllerId
                || original.Repetition != item.Repetition || item.Targets.IsDefault || item.Targets.Any(target => target == null)
                || !original.Targets.SequenceEqual(item.Targets.Select(Target))) return End("queue-changed");
            int index = original.Position;
            if (index <= position) return End("queue-reordered");
            position = index;
        }
        remainingQueue = nextQueue;
        if (outcomes.Count == members.Count) return End("completed");
        if (state.Decision == null) return CanWait(state) || End("unexpected-owned-input");
        if (OwnPriority(state)) return state.Stack.Length > 0 && selectedStack.Contains(state.Stack[0].Id) || End("uncaptured-stack-item");
        if (decline != null && state.Decision.Payload is PaymentPayload { OptionalDecline: { } offer }
            && SamePayment(offer) && OwnedDecline(state, offer) && declineMembers.Contains(offer.ProducingTriggerExecutionId)
            && AdmitOffer(offer)) return true;
        // The provider has proved there is no payable route. This mechanical
        // negative answer grants no permission to spend resources or answer a
        // different choice, and remains inside the captured execution fence.
        if (decline == null && UnpayableCapturedOffer(state) is { } unpayable && AdmitUnpayableOffer(unpayable)) return true;
        return End("unmatched-choice");
    }

    private OptionalDeclineOffer? UnpayableCapturedOffer(GameSnapshot state)
        => state.Decision?.Payload is PaymentPayload { OptionalDecline: { } offer,
            OptionalPaymentSupport.Availability: OptionalManaPaymentAvailability.Unpayable }
            && members.ContainsKey(offer.ProducingTriggerExecutionId) && OwnedDecline(state, offer) ? offer : null;

    private bool AdmitUnpayableOffer(OptionalDeclineOffer offer)
    {
        if (lastOfferId.Length > 0) return AdmitOffer(offer);
        seenOffers.Add(offer.OfferId); lastOfferId = offer.OfferId; lastOfferExecution = offer.ProducingTriggerExecutionId;
        return !answered.Contains(offer.ProducingTriggerExecutionId);
    }

    private bool AdmitOffer(OptionalDeclineOffer offer)
    {
        if (offer.OfferId == lastOfferId) return offer.ProducingTriggerExecutionId == lastOfferExecution;
        if (offer.PreviousOfferId != lastOfferId || seenOffers.Contains(offer.OfferId)
            || answered.Contains(offer.ProducingTriggerExecutionId)) return false;
        seenOffers.Add(offer.OfferId); lastOfferId = offer.OfferId; lastOfferExecution = offer.ProducingTriggerExecutionId;
        return true;
    }
    private bool SamePayment(OptionalDeclineOffer offer) => decline is { } original
        && offer.GroupKey == original.GroupKey && offer.PaymentKey == original.PaymentKey
        && offer.SourceCardId == original.SourceCardId && offer.Purpose == original.Purpose
        && offer.PayerId == original.PayerId && !offer.Mandatory;

    internal static bool OwnedDecline(GameSnapshot state, OptionalDeclineOffer offer)
    {
        if (state.Decision is not { Payload: PaymentPayload { Cancel.Enabled: true, OptionalDecline: { } current } } decision
            || current != offer || offer.Mandatory || offer.PayerId != state.ViewerId
            || state.DecisionOwnerId != state.ViewerId || offer.Purpose is not (PaymentPurpose.TriggeredAbilityCost or PaymentPurpose.EffectCost)
            || string.IsNullOrWhiteSpace(offer.GroupKey) || string.IsNullOrWhiteSpace(offer.PaymentKey)
            || string.IsNullOrWhiteSpace(offer.OfferId) || offer.PreviousOfferId == null || offer.OfferId == offer.PreviousOfferId
            || ExecutionFrames.Find(state, offer.ProducingTriggerExecutionId) is not { Kind: ExecutionKind.Trigger, Step: not ExecutionStep.Complete } producing
            || producing.SourceCardId != offer.SourceCardId) return false;
        string? id = decision.ExecutionId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(id) && visited.Add(id)) {
            if (id == offer.ProducingTriggerExecutionId) return true;
            id = ExecutionFrames.Find(state, id)?.ParentId;
        }
        return false;
    }

    private static bool OwnPriority(GameSnapshot state) => state.Decision is { Payload: PriorityPayload { Pass.Enabled: true } }
        && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
        && (state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId);
    private static bool CanWait(GameSnapshot state) => state.Decision == null
        && state.DecisionOwnerId != state.ViewerId && state.PriorityPlayerId != state.ViewerId;
    private static (string Kind, string Id, int? Player) Target(StackTarget target) => (target.Kind, target.Id, target.PlayerId);
    private bool End(string reason) { if (active) { active = false; stopReason = reason; } return false; }

    DecisionCommand? IFiniteDecisionPolicy.NextCommand(GameSnapshot state)
    {
        lock (gate) {
            if (paused || !Continue(state) || state.Revision < revision || state.Decision is not { } decision) return null;
            if (submittedScope == decision.Id) return null;
            if (OwnPriority(state)) {
                if (serverGrantId.Length > 0) return null;
                if (state.FiniteStackYield != null && state.Stack.Length <= FiniteStackYieldFacts.MaximumMembers
                    && state.Stack.All(item => selectedStack.Contains(item.Id))) {
                    serverGrantId = Guid.NewGuid().ToString("N");
                    serverMembers = state.Stack.Select(item => item.ExecutionId).ToImmutableArray();
                    var grant = new GrantFiniteStackYield(game, decision.Id, serverGrantId, serverMembers, actionVersion, choiceVersion);
                    if (GameCommandPermissions.Allows(state, grant)) return grant;
                    serverGrantId = ""; serverMembers = [];
                }
                string progress = string.Join('\n', state.Stack.Select(item => item.Id)) + ":" + outcomes.Count + ":" + answered.Count;
                if (submittedPriority == progress) { End("priority-without-progress"); return null; }
                var pass = new PassPriority(game, decision.Id) { StackInterruptionVersion = state.StackInterruptionVersion };
                return GameCommandPermissions.Allows(state, pass) ? pass : null;
            }
            return decision.Payload is PaymentPayload { OptionalDecline: { } offer } && (decline != null || UnpayableCapturedOffer(state) != null)
                && !answered.Contains(offer.ProducingTriggerExecutionId)
                ? new CancelPayment(game, decision.Id) : null;
        }
    }

    void IFiniteDecisionPolicy.Submitted(GameSnapshot state, DecisionCommand command)
    {
        lock (gate) {
            submittedScope = state.Decision!.Id;
            if (command is PassPriority)
                submittedPriority = string.Join('\n', state.Stack.Select(item => item.Id)) + ":" + outcomes.Count + ":" + answered.Count;
            if (command is CancelPayment && state.Decision.Payload is PaymentPayload { OptionalDecline: { } offer })
                answered.Add(offer.ProducingTriggerExecutionId);
        }
    }
}
