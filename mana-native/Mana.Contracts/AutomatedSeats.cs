using System.Collections.Immutable;

namespace Mana.Contracts;

// A host-only port. It is never supplied to the table renderer, a remote seat,
// or a policy. Each snapshot has exactly the knowledge of that automated seat.
public interface IAutomatedSeatProvider
{
    Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default);
    Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default);
    // Cancellation stops the caller's wait; it does not retract an action that
    // may already have reached the rules engine or authorize replaying it.
    Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default);
    Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default);
}

public sealed record AutomatedSeatObservation(int SeatId, GameSnapshot State,
    DecisionActionDomain Domain, DecisionSemantics Semantics);
