using System.Collections.Immutable;

namespace Mana.Contracts;

// A host-only port. It is never supplied to the table renderer, a remote seat,
// or a policy. Each snapshot has exactly the knowledge of that automated seat.
public interface IAutomatedSeatProvider
{
    Task EnableAutomatedSeatsAsync();
    Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync();
    Task ExecuteAutomatedAsync(int seatId, GameCommand command);
    Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode);
}

public sealed record AutomatedSeatObservation(int SeatId, GameSnapshot State,
    DecisionActionDomain Domain, DecisionSemantics Semantics);
