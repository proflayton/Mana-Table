namespace Mana.Contracts;

/// <summary>The provider revoked an observation while discovering its current
/// inputs. No snapshot or command authority was produced; observe afresh.</summary>
public sealed class EngineObservationSupersededException()
    : InvalidOperationException("The rules provider advanced while observing its current inputs.");
