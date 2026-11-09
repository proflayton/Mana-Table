namespace Mana.Contracts;

/// <summary>The provider revoked an observation while discovering its current
/// inputs. No snapshot or command authority was produced; observe afresh.</summary>
public sealed class EngineObservationSupersededException : InvalidOperationException
{
    public EngineObservationSupersededException()
        : base("The rules provider advanced while observing its current inputs.") { }

    public EngineObservationSupersededException(ObservationRetirement retirement) : this() => Retirement = retirement;

    public ObservationRetirement? Retirement { get; }

    public override string ToString() => Retirement is { } retirement
        ? base.ToString() + Environment.NewLine + "Observation retirement: " + retirement : base.ToString();
}
