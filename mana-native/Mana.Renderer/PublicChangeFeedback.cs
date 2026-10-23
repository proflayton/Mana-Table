using Mana.Contracts;

namespace Mana.Renderer;

public sealed record CounterChangeCue(long ActivityId, string TargetId, string TargetName,
    PublicCardChange Change, PublicEffectCause? Cause, double Started);
public sealed record EffectChangeGroup(string Id, PublicEffectCause? Cause, IReadOnlyList<CounterChangeCue> Changes, double Updated)
{
    public string Title => Cause?.Name is { Length: > 0 } name ? name : "Counter changes";
    public string Summary {
        get {
            var properties = Changes.GroupBy(change => (change.Change.Property, Added: change.Change.After > change.Change.Before)).ToArray();
            return string.Join(" · ", properties.Select(property => {
                var targets = property.GroupBy(change => change.TargetId)
                    .Select(target => target.Sum(change => (long)change.Change.After - change.Change.Before)).ToArray();
                long total = targets.Sum();
                if (targets.All(amount => amount == targets[0]))
                    return (total >= 0 ? "Added " : "Removed ") + Math.Abs(targets[0]) + " " + property.Key.Property
                        + (Math.Abs(targets[0]) == 1 ? " counter" : " counters") + (targets.Length == 1 ? " on 1 permanent" : " on each of " + targets.Length + " permanents");
                return (total >= 0 ? "Added " : "Removed ") + Math.Abs(total) + " " + property.Key.Property + " counters across " + targets.Length + " permanents";
            }));
        }
    }
}

/// <summary>Public effects are grouped only by their engine-issued cause identity, never by polling proximity.</summary>
public sealed class PublicChangeFeedback
{
    private readonly List<EffectChangeGroup> groups = [];
    public IReadOnlyList<EffectChangeGroup> Groups => groups.AsReadOnly();
    public EffectChangeGroup? Latest => groups.LastOrDefault();
    public void Clear() => groups.Clear();
    public void Observe(PublicActivityBatch activity, double now)
    {
        if (activity.IsBaseline) Clear();
        foreach (var entry in activity.Entries.Where(entry => entry.Change is { Kind: "counters" }
            && entry.CardId is { Length: > 0 })) {
            var change = entry.Change!;
            if (change.Before == change.After) continue;
            string key = entry.Cause?.Id is { Length: > 0 } causeId ? "cause:" + causeId : "event:" + entry.Id;
            var cue = new CounterChangeCue(entry.Id, entry.CardId!, entry.CardName ?? "Permanent", change, entry.Cause, now);
            int index = groups.FindIndex(group => group.Id == key);
            if (index < 0) groups.Add(new(key, entry.Cause, Array.AsReadOnly(new[] { cue }), now));
            else {
                var previous = groups[index];
                groups.RemoveAt(index);
                groups.Add(previous with { Changes = Array.AsReadOnly(previous.Changes.Append(cue).ToArray()), Updated = now });
            }
        }
        while (groups.Count > 8) groups.RemoveAt(0);
        // Completed public events remain historical facts. Painting resolves
        // their opaque IDs against the current frame, never retained card faces.
    }
}
