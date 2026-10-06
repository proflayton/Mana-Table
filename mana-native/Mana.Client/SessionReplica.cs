namespace Mana.Client;

/// <summary>A game-independent replica. It accepts full snapshots, never simulates game rules.</summary>
public sealed class SessionReplica<T>(Func<T, string> identity, Func<T, long> revision) where T : class
{
    private readonly HashSet<string> retired = [];
    public T? Current { get; private set; }
    public long Epoch { get; private set; }
    public long BeginCommand() => ++Epoch;
    public bool Accept(T? next, long expectedEpoch)
    {
        if (expectedEpoch != Epoch) return false;
        if (next != null && retired.Contains(identity(next))) return false;
        if (next != null && Current != null && identity(next) == identity(Current) && revision(next) < revision(Current)) return false;
        if (Current != null && (next == null || identity(Current) != identity(next))) retired.Add(identity(Current));
        Current = next;
        return true;
    }
}
