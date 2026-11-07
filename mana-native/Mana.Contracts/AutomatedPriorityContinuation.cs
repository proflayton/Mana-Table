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
public enum AutomatedPriorityControlResult { Accepted, Retired }

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
