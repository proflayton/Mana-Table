using System.Collections.Immutable;

namespace Mana.Contracts;

// Simulation is an explicit rules-provider capability, separate from joining a
// live player's seat. Observations are public and carry no decision authority.
public interface IAutomatedSimulation
{
    Task<GameSnapshot> StartSimulationAsync(ImmutableArray<string> deckIds);
    Task<GameSnapshot?> ObserveSimulationAsync();
    Task StopSimulationAsync(string gameId);
}
