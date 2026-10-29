using Mana.Contracts;
using System.Collections.Immutable;

namespace Mana.Table.Presentation;

// Ordered, noninteractive public announcements. No card face, input key or
// executable snapshot survives here. Current decisions always stay authoritative.
internal sealed class StackArrivalPresentation
{
    internal const double Duration = 2.4;
    internal const int Capacity = 512;
    internal sealed record Announcement(string Id, string? StackId, string SourceId, string Name, string Text,
        int? ControllerId, StackActionKind? Kind, string EventKind)
    {
        internal string ExecutionId { get; init; } = "";
        internal bool AwaitingMetadata { get; init; }
        internal int CapturedCount { get; init; } = 1;
    }
    private readonly Queue<Announcement> pending = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> recent = new();
    private Announcement? current;
    private double elapsed, lastTick;
    private bool holding, presented;
    private readonly Dictionary<string, string> finiteGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> finiteCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> finitePresented = new(StringComparer.Ordinal);
    internal int IncompleteCount { get; private set; }
    internal int PendingCount => pending.Count + (current == null ? 0 : 1);

    // A deliberate finite gesture changes the presentation of only its exact
    // captured executions. The public execution/activity streams remain intact.
    internal void BeginFinite(IEnumerable<(string Execution, string Group)> captured, double now)
    {
        EndFinite();
        foreach (var (execution, group) in captured)
            if (execution.Length > 0 && group.Length > 0) finiteGroups.TryAdd(execution, group);
        foreach (var group in finiteGroups.Values) finiteCounts[group] = finiteCounts.GetValueOrDefault(group) + 1;
        Tick(now);
        var queued = (current == null ? pending.ToArray() : new[] { current }.Concat(pending)).ToArray();
        current = null; pending.Clear(); elapsed = 0; lastTick = now; presented = false;
        foreach (var announcement in queued) {
            if (GroupFinite(announcement) is not { } grouped) continue;
            if (current == null) current = grouped; else pending.Enqueue(grouped);
        }
    }

    internal void EndFinite()
    {
        finiteGroups.Clear(); finiteCounts.Clear(); finitePresented.Clear();
    }

    internal void Observe(GameSnapshot? before, GameSnapshot after, double now,
        ImmutableArray<ActivityEntry> events = default, bool historyGap = false,
        ImmutableArray<ExecutionTransition> executions = default, bool executionBaseline = false, bool executionGap = false)
    {
        if (before == null || before.Id != after.Id || before.ViewerId != after.ViewerId
            || after.Status is "finished" or "error") { Clear(); return; }
        if (executionBaseline) { Clear(); if (executionGap) IncompleteCount++; return; }
        Tick(now);
        if (historyGap) IncompleteCount++;
        var previous = before.Stack.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var arrivals = after.Stack.Where(item => item.Id.Length > 0 && !previous.Contains(item.Id)).ToArray();
        var urgent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in executions.IsDefault ? [] : executions) {
            if (transition.Step is not (ExecutionStep.Stack or ExecutionStep.Complete)
                || ExecutionFrames.Find(after, transition.ExecutionId) is not { } occurrence
                || occurrence.Kind is ExecutionKind.Mana or ExecutionKind.Rules) continue;
            var item = after.Stack.FirstOrDefault(item => item.ExecutionId == occurrence.Id);
            var entry = (events.IsDefault ? [] : events).LastOrDefault(entry => entry.ExecutionId == occurrence.Id
                && (transition.Step == ExecutionStep.Complete ? entry.Kind == "resolved" : entry.Kind is "cast" or "ability" or "triggered"));
            var kind = occurrence.Kind switch { ExecutionKind.Spell => StackActionKind.Spell,
                ExecutionKind.Activation => StackActionKind.ActivatedAbility, ExecutionKind.Trigger => StackActionKind.TriggeredAbility,
                _ => StackActionKind.Ability };
            string eventKind = transition.Step == ExecutionStep.Complete ? transition.Outcome?.ToString().ToLowerInvariant() ?? "completed"
                : occurrence.Kind switch { ExecutionKind.Spell => "cast", ExecutionKind.Trigger => "triggered", _ => "ability" };
            string key = "execution:" + occurrence.Id + ":" + transition.Sequence;
            Add(new(key, item?.Id ?? entry?.StackId, occurrence.SourceCardId ?? "", occurrence.SourceName ?? "Ability",
                item?.Text ?? (entry?.Detail is { Length: > 0 } detail ? detail : entry?.Message ?? ""),
                occurrence.ActorPlayerId, kind, eventKind) {
                    ExecutionId = occurrence.Id,
                    AwaitingMetadata = transition.Step == ExecutionStep.Stack && item == null && string.IsNullOrWhiteSpace(entry?.Detail)
                }, now);
            if (transition.Step == ExecutionStep.Stack && item != null && occurrence.Step != ExecutionStep.Complete
                && kind is StackActionKind.Spell or StackActionKind.ActivatedAbility) urgent.Add(key);
        }
        foreach (var entry in events.IsDefault ? [] : events) {
            if (entry.ExecutionId.Length > 0) continue;
            if (entry.Kind is not ("cast" or "ability" or "triggered" or "resolved")) continue;
            if (!string.IsNullOrEmpty(entry.StackId) && arrivals.FirstOrDefault(item => item.Id == entry.StackId) is { } linked) {
                AddStack(linked); continue;
            }
            // This optional join is emitted from the actual engine stack
            // occurrence. Legacy events remain separate public facts.
            string key = !string.IsNullOrEmpty(entry.StackId) ? "stack:" + entry.StackId : "event:" + entry.Id;
            Add(new(key, entry.StackId, entry.CardId ?? entry.Cause?.CardId ?? "", entry.CardName ?? entry.Cause?.Name ?? "",
                entry.Detail.Length > 0 ? entry.Detail : entry.Message, entry.Cause?.ControllerId ?? entry.PlayerId, null, entry.Kind), now);
        }
        foreach (var item in arrivals.Reverse().Where(item => item.ExecutionId.Length == 0)) AddStack(item);
        // A new real response must take attention immediately. It is not an
        // old queued trigger merely because that simultaneous burst was large.
        urgent.UnionWith(arrivals.Where(item => item.ExecutionId.Length == 0 && item.Kind is StackActionKind.Spell or StackActionKind.ActivatedAbility)
            .Select(item => "stack:" + item.Id));
        EnrichDeferred(after, urgent, now);
        Promote(urgent, now);

        void AddStack(StackItem item)
        {
            var source = item.Card is { FaceDown: false } card ? CardViews.Identity(card) : "";
            Add(new("stack:" + item.Id, item.Id, source, item.Name, item.Text, item.ControllerId, item.Kind, ""), now);
        }
    }

    private void EnrichDeferred(GameSnapshot state, HashSet<string> urgent, double now)
    {
        bool wasWaiting = current?.AwaitingMetadata == true;
        current = Enrich(current);
        var queued = pending.ToArray(); pending.Clear();
        foreach (var announcement in queued) if (Enrich(announcement) is { } ready) pending.Enqueue(ready);
        if (current == null) current = pending.TryDequeue(out var next) ? next : null;
        if (wasWaiting && current?.AwaitingMetadata != true) { elapsed = 0; lastTick = now; }

        Announcement? Enrich(Announcement? announcement)
        {
            if (announcement?.AwaitingMetadata != true) return announcement;
            var occurrence = ExecutionFrames.Find(state, announcement.ExecutionId);
            var item = state.Stack.FirstOrDefault(item => item.ExecutionId == announcement.ExecutionId);
            var entry = state.Activity.LastOrDefault(entry => entry.ExecutionId == announcement.ExecutionId
                && entry.Kind is "cast" or "ability" or "triggered" or "resolved" && !string.IsNullOrWhiteSpace(entry.Detail));
            if (item != null || entry != null) {
                if (item != null && occurrence?.Step != ExecutionStep.Complete
                    && announcement.Kind is StackActionKind.Spell or StackActionKind.ActivatedAbility) urgent.Add(announcement.Id);
                return announcement with { StackId = item?.Id ?? entry?.StackId,
                    Name = item?.Name ?? occurrence?.SourceName ?? entry?.CardName ?? announcement.Name,
                    Text = item?.Text ?? entry!.Detail, AwaitingMetadata = false };
            }
            if (occurrence == null || occurrence.Step == ExecutionStep.Complete) {
                // Retained history no longer supplies the reading material.
                // Report that loss instead of blocking forever or painting an
                // empty, uninspectable stack surface. The terminal fact keeps
                // its separate ordered announcement.
                IncompleteCount++; return null;
            }
            return announcement;
        }
    }

    private void Promote(HashSet<string> keys, double now)
    {
        if (keys.Count == 0) return;
        var queued = pending.ToArray();
        var urgent = (current == null ? queued : new[] { current }.Concat(queued)).Where(item => keys.Contains(item.Id)).ToArray();
        if (urgent.Length == 0) return;
        pending.Clear();
        foreach (var item in urgent.Skip(1)) pending.Enqueue(item);
        if (current != null && !keys.Contains(current.Id)) pending.Enqueue(current);
        foreach (var item in queued.Where(item => !keys.Contains(item.Id))) pending.Enqueue(item);
        current = urgent[0]; elapsed = 0; lastTick = now;
    }

    private void Add(Announcement announcement, double now)
    {
        if (!seen.Add(announcement.Id)) return;
        recent.Enqueue(announcement.Id);
        while (recent.Count > 2048) seen.Remove(recent.Dequeue());
        if (GroupFinite(announcement) is not { } grouped) return;
        announcement = grouped;
        if (PendingCount >= Capacity) { IncompleteCount++; return; }
        if (PendingCount == 0) presented = false;
        pending.Enqueue(announcement);
        if (current == null) { current = pending.Dequeue(); elapsed = 0; lastTick = now; }
    }

    private Announcement? GroupFinite(Announcement announcement)
    {
        if (!finiteGroups.TryGetValue(announcement.ExecutionId, out string? group)) return announcement;
        if (!finitePresented.Add(group)) return null;
        return announcement with { CapturedCount = finiteCounts[group] };
    }

    private void Tick(double now)
    {
        if (current == null) { lastTick = now; return; }
        if (current.AwaitingMetadata) { lastTick = now; return; }
        if (!holding) elapsed += Math.Max(0, now - lastTick);
        lastTick = now;
        if (elapsed < Duration) return;
        presented = true;
        current = pending.TryDequeue(out var next) ? next : null;
        elapsed = 0;
    }
    internal void Hold(double now, bool value) { Tick(now); holding = value; }
    internal Announcement? Current(double now) { Tick(now); return current?.AwaitingMetadata == true ? null : current; }
    internal float Strength(string stackId, double now)
    {
        Tick(now);
        return current?.StackId == stackId ? (float)Math.Max(0, 1 - elapsed / Duration) : 0;
    }
    internal bool Ready(double now) { Tick(now); return current == null; }
    // Explicit finite-stack intent may proceed after the first readable beat.
    // Remaining arrivals stay visible as a census/announcement queue; every
    // actual settled engine change still owns its independent pacing interval.
    internal bool ProgressReady(double now) { Tick(now); return current == null || presented; }
    internal void Clear()
    {
        current = null; pending.Clear(); seen.Clear(); recent.Clear();
        EndFinite();
        elapsed = lastTick = 0; holding = presented = false; IncompleteCount = 0;
    }
}
