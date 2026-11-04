namespace Mana.Contracts;

// A read-only method's closed outcome. No case is a command receipt or
// permission to resubmit; Changed cannot certify a safe stale observation.
public enum EngineSourceReadFailure { Superseded, Changed, Failed }
public sealed class EngineSourceReadException : IOException
{
    public EngineSourceReadFailure Reason { get; }
    public EngineSourceReadException(EngineSourceReadFailure reason) : base(reason switch {
        EngineSourceReadFailure.Superseded => "The source observation was superseded before the query.",
        EngineSourceReadFailure.Changed => "The source input changed during the query.",
        EngineSourceReadFailure.Failed => "The source query failed.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    }) => Reason = reason;
}
