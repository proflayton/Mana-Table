namespace Mana.Contracts;

/// <summary>On-demand facts for this connection's own audience. The provider
/// selects the seat; callers cannot name another player. Ordinary observations
/// remain independent of action discovery. Unsupported and superseded reads
/// grant no authority and never authorize replaying a command.</summary>
public interface IOwnedDecisionObservations
{
    bool SupportsOwnedDecisionObservations => true;
    Task<OwnedDecisionObservation?> ObserveDecisionAsync(CancellationToken cancellation = default);
}

public sealed record OwnedDecisionObservation(GameSnapshot State,
    DecisionActionDomain Domain, DecisionSemantics Semantics);
