using System.Collections.Immutable;

namespace Mana.Contracts;

// Host composition only. A policy can never acquire this connection or replace
// a decision with metadata. Quiet, finite yielding and gameplay are deliberately
// not representable on this optional port.
public interface IAutomatedPriorityContinuation
{
    bool SupportsAutomatedPriorityContinuation { get; }
    long AutomatedPriorityEpoch { get; }
    Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId, AutomatedPriorityControl control,
        CancellationToken cancellation = default);
}

// Retired means a known rejected admission or a demonstrably retired world. A
// completed old-world operation does not acquire authority in its replacement.
// An uncertain/lost current-world receipt still fails instead of returning this.
public abstract record AutomatedPriorityControlResult
{
    private protected AutomatedPriorityControlResult() { }
    public sealed record Accepted(AutomatedPriorityReceipt Receipt) : AutomatedPriorityControlResult;
    public sealed record Retired : AutomatedPriorityControlResult;
}

public enum AutomatedPriorityMatchStatus { Starting, Playing, Resolving, Finished, Error }

// A control receipt can reconcile host consent only. It carries neither a
// rules observation nor action offers, card faces, timers or policy authority.
public sealed record AutomatedPriorityReceipt(long Epoch, string GameId, int SeatId, long Revision,
    long BoardRevision, long StackInterruptionVersion, AutomatedPriorityMatchStatus Status,
    bool PlayerActive, int? DecisionOwnerId, int? PriorityPlayerId, string? DecisionId, string? PriorityDecisionId,
    PriorityContinuationState Continuation, PriorityObservationState Observation)
{
    public void Validate()
    {
        if (Epoch <= 0 || !PriorityContinuationFacts.Identifier(GameId) || SeatId < 0 || Revision < 0 || BoardRevision < 0
            || StackInterruptionVersion < 0 || !Enum.IsDefined(Status)
            || DecisionOwnerId is < 0 || PriorityPlayerId is < 0
            || DecisionId != null && !PriorityContinuationFacts.Identifier(DecisionId)
            || PriorityDecisionId != null && (PriorityDecisionId != DecisionId || Status != AutomatedPriorityMatchStatus.Playing)
            || Observation == null || Continuation == null
            || Observation.BoardRevision != BoardRevision || Observation.PublicActivitySequence < 0
            || Observation.AcknowledgedBoardRevision < 0 || Observation.AcknowledgedBoardRevision > BoardRevision
            || Observation.AcknowledgedPublicActivitySequence < 0
            || Observation.AcknowledgedPublicActivitySequence > Observation.PublicActivitySequence
            || Continuation.Sequence < 0 || !Enum.IsDefined(Continuation.Status)
            || Continuation.Mode is { } mode && !Enum.IsDefined(mode)
            || Continuation.AutomaticPasses < 0 || Continuation.ReasonCode == null
            || Continuation.ReasonCode.Length > 128 || Continuation.ReasonCode.Any(char.IsControl)
            || !PriorityContinuationFacts.Stops(Continuation.PhaseStops) || Continuation.GrantId == null
            || Continuation.GrantId.Length == 0 && (Continuation.Mode != null || Continuation.AutomaticPasses != 0
                || !Continuation.PhaseStops.IsEmpty || Continuation.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)
            || Continuation.GrantId.Length > 0 && (!PriorityContinuationFacts.Identifier(Continuation.GrantId)
                || Continuation.Mode == null || Continuation.Sequence == 0 || Continuation.Status == PriorityContinuationStatus.Idle))
            throw new ArgumentException("An automated control receipt must preserve its exact owner and closed metadata.");
    }

    public bool SamePriorityInput(GameSnapshot observed) => Status == AutomatedPriorityMatchStatus.Playing && PlayerActive
        && observed.Status == "playing" && observed.Viewer is { Eliminated: false } && observed.Turn > 0
        && observed.Id == GameId && observed.ViewerId == SeatId && Revision >= observed.Revision
        && (observed.DecisionOwnerId == null || observed.DecisionOwnerId == observed.ViewerId)
        && (observed.PriorityPlayerId == null || observed.PriorityPlayerId == observed.ViewerId)
        && (DecisionOwnerId == null || DecisionOwnerId == SeatId) && (PriorityPlayerId == null || PriorityPlayerId == SeatId)
        && BoardRevision == observed.BoardRevision && StackInterruptionVersion == observed.StackInterruptionVersion
        && observed.Decision is { Payload: PriorityPayload { Pass.Enabled: true } } decision
        && PriorityDecisionId == decision.Id && DecisionId == decision.Id
        && Observation.Registered && !Observation.Overflowed;
}

public abstract record AutomatedPriorityControl
{
    public string GameId { get; init; }
    private protected AutomatedPriorityControl(string gameId) => GameId = gameId;
    public sealed record Acknowledge(string GameId, long BoardRevision, long PublicActivitySequence)
        : AutomatedPriorityControl(GameId);
    public sealed record NoResponse(string GameId, string DecisionId, long Sequence, string GrantId,
        ImmutableArray<string> PhaseStops) : AutomatedPriorityControl(GameId);
    public sealed record Withdraw(string GameId, long ThroughSequence) : AutomatedPriorityControl(GameId);

    public void Validate()
    {
        bool valid = !string.IsNullOrWhiteSpace(GameId) && this switch {
            Acknowledge value => value.BoardRevision >= 0 && value.PublicActivitySequence >= 0,
            NoResponse value => PriorityContinuationFacts.Identifier(value.DecisionId) && value.Sequence > 0
                && PriorityContinuationFacts.Identifier(value.GrantId) && PriorityContinuationFacts.Stops(value.PhaseStops),
            Withdraw value => value.ThroughSequence > 0,
            _ => false
        };
        if (!valid) throw new ArgumentException("An automated priority control needs its exact owned scope and bounded guards.");
    }
}
