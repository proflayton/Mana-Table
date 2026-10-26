using Mana.Contracts;

namespace Mana.Table.Presentation;

// A transient emphasis on actual new stack occurrences. It retains no card
// faces or command capabilities, and never replays retained history on joining.
internal sealed class StackArrivalPresentation
{
    internal const double Duration = 2.4;
    private string? id;
    private double started;

    internal void Observe(GameSnapshot? before, GameSnapshot after, double now)
    {
        if (before == null || before.Id != after.Id || before.ViewerId != after.ViewerId
            || after.Status is "finished" or "error") { Clear(); return; }
        if (id != null && !after.Stack.Any(item => item.Id == id)) Clear();
        var previous = before.Stack.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var arrival = after.Stack.FirstOrDefault(item => item.Id.Length > 0 && !previous.Contains(item.Id));
        if (arrival == null) return;
        id = arrival.Id; started = now;
    }

    internal float Strength(string stackId, double now) => id == stackId && now >= started && now < started + Duration
        ? (float)(1 - (now - started) / Duration) : 0;
    internal bool Ready(double now) => id == null || now >= started + Duration;
    internal void Clear() { id = null; started = 0; }
}
