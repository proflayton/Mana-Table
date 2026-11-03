namespace Mana.Client;

/// <summary>A game-independent replica. It accepts full snapshots, never simulates game rules.</summary>
public sealed class SessionReplica<T>(Func<T, string> identity, Func<T, long> revision) where T : class
{
    private readonly HashSet<string> retired = [];
    public T? Current { get; private set; }
    public long Epoch { get; private set; }
    public long BeginCommand() => ++Epoch;
    // Admission is checked before retiring dependent work; Accept rechecks it
    // at commit rather than treating this current-state result as a capability.
    internal bool CanAccept(T? next, long expectedEpoch) => expectedEpoch == Epoch
        && (next == null || !retired.Contains(identity(next)))
        && (next == null || Current == null || identity(next) != identity(Current) || revision(next) >= revision(Current));
    public bool Accept(T? next, long expectedEpoch)
    {
        if (!CanAccept(next, expectedEpoch)) return false;
        if (Current != null && (next == null || identity(Current) != identity(next))) retired.Add(identity(Current));
        Current = next;
        return true;
    }
}
