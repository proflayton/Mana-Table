using Mana.Contracts;

namespace Mana.Renderer;

public sealed record ResolutionCue(string Title, string Detail, string[] Changes, double Started);

/// <summary>Readable public event feedback. It reports events; it never infers what a spell should do.</summary>
public sealed class ActionFeedback
{
    public ResolutionCue? Last { get; private set; }
    public void Clear() => Last = null;
    public void Observe(GameSnapshot? before, GameSnapshot after, double now)
    {
        if (before == null || before.Id != after.Id) { Clear(); return; }
        long last = before.Activity.LastOrDefault()?.Id ?? 0;
        var entries = after.Activity.Where(e => e.Id > last).ToArray();
        var resolved = entries.LastOrDefault(e => e.Kind == "resolved");
        if (resolved == null) return;
        // Recent changes are labelled as such, not attributed to an inferred cause.
        var changes = entries.Where(e => e.Kind is "moved" or "arrived" or "life" or "damage" or "counters")
            .Select(e => e.Message).Distinct().ToArray();
        Last = new(resolved.Message, resolved.Detail, changes, now);
    }
}
