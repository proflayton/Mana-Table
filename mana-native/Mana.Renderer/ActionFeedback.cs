using Mana.Contracts;

namespace Mana.Renderer;

public sealed record ResolutionCue(string Title, string Detail, string[] Changes, double Started)
{
    public string Outcome { get; init; } = "";
    public string Heading => Outcome switch { "resolved" => "JUST RESOLVED", "notResolved" => "DID NOT RESOLVE", _ => "REPORTED OUTCOME" };
}
public sealed record PublicOutcomeCue(string[] Messages, double Started);

/// <summary>Readable public event feedback. It reports events; it never infers what a spell should do.</summary>
public sealed class ActionFeedback
{
    public ResolutionCue? Last { get; private set; }
    public PublicOutcomeCue? LastPublicOutcome { get; private set; }
    public void Clear() { Last = null; LastPublicOutcome = null; }
    public void Observe(PublicActivityBatch activity, double now)
    {
        if (activity.IsBaseline) { Clear(); return; }
        var after = activity.After;
        var entries = activity.Entries;
        var publicEvents = entries.Where(e => e.Kind is "draw" or "shuffle" && e.Count > 0).ToArray();
        var countedMessages = publicEvents.GroupBy(e => (e.PlayerId, e.Kind)).Select(group => {
            int count = (int)Math.Min(int.MaxValue, group.Sum(e => (long)e.Count));
            var actor = after.Players.FirstOrDefault(p => p.Id == group.Key.PlayerId);
            int seat = actor == null ? -1 : after.Players.IndexOf(actor);
            string player = group.Key.PlayerId == after.ViewerId ? "You" : actor == null ? "A player" : actor.Name + " · Seat " + (seat + 1);
            return group.Key.Kind == "draw" ? player + " drew " + count + (count == 1 ? " card." : " cards.")
                : player + (group.Key.PlayerId == after.ViewerId ? " shuffled your library." : " shuffled their library.") + (count > 1 ? " (" + count + " times)" : "");
        });
        // These are explicit public state changes reported by the engine, not
        // deductions from life totals, card text or snapshot differences.
        var messages = entries.Where(e => e.Kind == "playerState" && !string.IsNullOrWhiteSpace(e.Message))
            .Select(e => e.Message).Concat(countedMessages).Distinct().ToArray();
        if (messages.Length > 0) LastPublicOutcome = new(messages, now);
        var resolved = entries.LastOrDefault(e => e.Kind == "resolved");
        if (resolved == null) return;
        // Recent changes are labelled as such, not attributed to an inferred cause.
        // A resolution can arrive in the same poll as draw events. Keep the
        // counted result first in the visible resolution cue instead of hiding
        // it behind a separate, equally recent public-outcome cue. Use only new
        // events from this poll; an older draw does not belong to a later spell.
        var changes = messages.Concat(entries.Where(e => e.Kind is "moved" or "arrived" or "life" or "damage" or "counters")
            .Select(e => e.Message)).Distinct().ToArray();
        Last = new(resolved.Message, resolved.Detail, changes, now) { Outcome = resolved.Outcome };
    }
}
