using Mana.Contracts;

namespace Mana.Renderer;

public sealed record ResolutionCue(string Title, string Detail, string[] Changes, double Started)
{
    public string ExecutionId { get; init; } = "";
    public bool HistoryIncomplete { get; init; }
    public string Outcome { get; init; } = "";
    public string Heading => Outcome switch { "resolved" => "JUST RESOLVED", "notResolved" => "DID NOT RESOLVE", _ => "REPORTED OUTCOME" };
}
public sealed record PublicOutcomeCue(string[] Messages, double Started);

/// <summary>Readable public event feedback. It reports events; it never infers what a spell should do.</summary>
public sealed class ActionFeedback
{
    private readonly Dictionary<string, List<ActivityEntry>> executing = new(StringComparer.Ordinal);
    private readonly HashSet<string> incomplete = new(StringComparer.Ordinal);
    private readonly List<ResolutionCue> recent = [];
    public ResolutionCue? Last { get; private set; }
    public IReadOnlyList<ResolutionCue> Recent => recent.AsReadOnly();
    public PublicOutcomeCue? LastPublicOutcome { get; private set; }
    public void Clear() { Last = null; LastPublicOutcome = null; executing.Clear(); incomplete.Clear(); recent.Clear(); }
    public void Observe(PublicActivityBatch activity, double now)
    {
        if (activity.IsBaseline || activity.ExecutionBaseline) {
            Clear();
            foreach (var execution in activity.After.Executions?.Occurrences.Where(item => item.Step != ExecutionStep.Complete) ?? [])
                incomplete.Add(execution.Id);
            return;
        }
        var after = activity.After;
        var entries = activity.Entries;
        // These are explicit public state changes reported by the engine, not
        // deductions from life totals, card text or snapshot differences.
        var messages = entries.Where(e => e.Kind == "playerState" && !string.IsNullOrWhiteSpace(e.Message))
            .Select(e => e.Message).Concat(CountedMessages(entries, after)).Distinct().ToArray();
        if (messages.Length > 0) LastPublicOutcome = new(messages, now);
        foreach (var entry in entries.Where(entry => entry.ExecutionId.Length > 0)) {
            if (!executing.TryGetValue(entry.ExecutionId, out var changes)) executing.Add(entry.ExecutionId, changes = []);
            changes.Add(entry);
            if (changes.Count > 512) { changes.RemoveAt(0); incomplete.Add(entry.ExecutionId); }
        }
        if (activity.HasHistoryGap) {
            incomplete.UnionWith(executing.Keys);
            incomplete.UnionWith(activity.Executions.Select(item => item.ExecutionId));
            incomplete.UnionWith(after.Executions?.Occurrences.Where(item => item.Step != ExecutionStep.Complete)
                .Select(item => item.Id) ?? []);
        }
        // The provider's bounded occurrence window also bounds unfinished
        // feedback. No retained portrait or command capability lives here.
        var retained = after.Executions?.Occurrences.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        incomplete.IntersectWith(retained);
        foreach (var id in executing.Keys.Where(id => !retained.Contains(id)).ToArray()) { executing.Remove(id); incomplete.Remove(id); }
        foreach (var transition in activity.Executions.Where(item => item.Step == ExecutionStep.Complete)) {
            if (ExecutionFrames.Find(after, transition.ExecutionId) is not { } occurrence) continue;
            executing.Remove(occurrence.Id, out var changes);
            var resolved = changes?.LastOrDefault(entry => entry.Kind == "resolved");
            string outcome = transition.Outcome == ExecutionOutcome.Resolved ? "resolved" : "notResolved";
            var costs = (changes ?? []).Where(entry => entry.ExecutionStep == ExecutionStep.Cost).ToArray();
            var effects = (changes ?? []).Where(entry => entry.ExecutionStep != ExecutionStep.Cost).ToArray();
            var results = ChangeMessages(costs).Concat(CountedMessages(costs, after)).Select(message => "Cost: " + message)
                .Concat(CountedMessages(effects, after)).Concat(ChangeMessages(effects)).ToArray();
            Add(new(resolved?.Message ?? (occurrence.SourceName ?? "Ability") + " " + (transition.Outcome?.ToString().ToLowerInvariant() ?? "completed") + ".",
                resolved?.Detail ?? "", results, now) { ExecutionId = occurrence.Id, Outcome = outcome,
                    HistoryIncomplete = incomplete.Remove(occurrence.Id) });
        }
        // Legacy resolutions remain independent facts. Sharing a network poll
        // never assigns its other changes to that spell or ability.
        foreach (var resolved in entries.Where(entry => entry.ExecutionId.Length == 0 && entry.Kind == "resolved"))
            Add(new(resolved.Message, resolved.Detail, [], now) { Outcome = resolved.Outcome });

        void Add(ResolutionCue cue) {
            Last = cue; recent.Add(cue);
            while (recent.Count > 32) recent.RemoveAt(0);
        }
    }

    private static IEnumerable<string> ChangeMessages(IEnumerable<ActivityEntry> entries) => entries
        .Where(entry => entry.Kind is "playerState" or "moved" or "arrived" or "life" or "damage" or "counters")
        .Select(entry => entry.Message);

    private static IEnumerable<string> CountedMessages(IEnumerable<ActivityEntry> entries, GameSnapshot state) => entries
        .Where(entry => entry.Kind is "draw" or "shuffle" && entry.Count > 0).GroupBy(entry => (entry.PlayerId, entry.Kind))
        .Select(group => {
            int count = (int)Math.Min(int.MaxValue, group.Sum(entry => (long)entry.Count));
            var actor = state.Players.FirstOrDefault(player => player.Id == group.Key.PlayerId);
            string player = group.Key.PlayerId == state.ViewerId ? "You" : actor == null ? "A player"
                : actor.Name + " · Seat " + (state.Players.IndexOf(actor) + 1);
            return group.Key.Kind == "draw" ? player + " drew " + count + (count == 1 ? " card." : " cards.")
                : player + (group.Key.PlayerId == state.ViewerId ? " shuffled your library." : " shuffled their library.")
                    + (count > 1 ? " (" + count + " times)" : "");
        });
}
