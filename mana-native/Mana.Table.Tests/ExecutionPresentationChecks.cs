using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class ExecutionPresentationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var initial = PresentationSmoke.Example() with { Activity = [], Stack = [], Decision = null,
            Executions = new(1, 0, [], []) };
        var source = initial.Viewer!.Zone("Battlefield").Cards.First();
        var cursor = new ExecutionCursor(); var activity = new PublicActivityCursor();
        var feedback = new ActionFeedback(); var counters = new PublicChangeFeedback(); var announcements = new StackArrivalPresentation();
        GameSnapshot? before = null;
        void Observe(GameSnapshot state, double now, bool baseline = false) {
            var execution = cursor.Observe(state, baseline);
            var batch = activity.Observe(before, state, execution.Transitions, execution.Baseline, execution.Gap);
            feedback.Observe(batch, now); counters.Observe(batch, now);
            announcements.Observe(before, state, now, batch.Entries, batch.HasHistoryGap,
                batch.Executions, batch.ExecutionBaseline, batch.ExecutionGap);
            before = state;
        }
        ActivityEntry Event(long sequence, string id, ExecutionStep step, string kind, string message, int count = 0) =>
            new(sequence, initial.Turn, initial.PhaseKey, kind, initial.ViewerId, message, source.VisualId, source.Name) {
                ExecutionId = id, ExecutionStep = step, Count = count };
        var activation = new ExecutionOccurrence("activation", "activation", ExecutionKind.Activation, ExecutionStep.Cost) {
            ActorPlayerId = initial.ViewerId, SourceCardId = source.VisualId, SourceName = source.Name };
        var cost = Event(1, activation.Id, ExecutionStep.Cost, "counters", "Removed one counter.") with {
            Change = new("counters", "+1/+1", 3, 2), Cause = new(source.VisualId, source.Name, "Draw a card.", initial.ViewerId, Id: "old-cause") };
        var started = initial with { Executions = new(1, 2, [activation], [new(1, activation.Id, ExecutionStep.Preparation),
            new(2, activation.Id, ExecutionStep.Cost)]), Activity = [cost] };
        Observe(initial, 0); Observe(started, 1);
        check(feedback.Last == null && counters.Groups.Single().Id == "execution:activation" && announcements.PendingCount == 0,
            "An actual counter payment is feedback on its execution, without announcing resolution or inferring a stack entry");

        var mana = new ExecutionOccurrence("mana", activation.Id, ExecutionKind.Mana, ExecutionStep.Complete) {
            ParentId = activation.Id, Outcome = ExecutionOutcome.Resolved, ActorPlayerId = initial.ViewerId };
        var other = new ExecutionOccurrence("other", "other", ExecutionKind.Spell, ExecutionStep.Complete) {
            Outcome = ExecutionOutcome.Resolved, ActorPlayerId = initial.ViewerId, SourceName = "Another spell" };
        var settled = started with { Executions = new(1, 9,
            [activation with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }, mana, other],
            [.. started.Executions!.Transitions, new(3, mana.Id, ExecutionStep.Preparation),
                new(4, mana.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                new(5, activation.Id, ExecutionStep.Stack), new(6, activation.Id, ExecutionStep.Resolution),
                new(7, activation.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                new(8, other.Id, ExecutionStep.Stack), new(9, other.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
            Activity = [cost, Event(2, activation.Id, ExecutionStep.Resolution, "draw", "You drew a card.", 1),
                Event(3, other.Id, ExecutionStep.Resolution, "draw", "You drew cards.", 7),
                Event(4, activation.Id, ExecutionStep.Resolution, "draw", "You drew cards.", 2),
                Event(5, activation.Id, ExecutionStep.Complete, "resolved", "Activation resolved.") with { Detail = "Draw three cards." },
                Event(6, other.Id, ExecutionStep.Complete, "resolved", "Another spell resolved.") with { Detail = "Draw seven cards." }] };
        Observe(settled, 2);
        var result = feedback.Recent.Single(item => item.ExecutionId == activation.Id);
        var otherResult = feedback.Recent.Single(item => item.ExecutionId == other.Id);
        check(result.Changes.SequenceEqual(new[] { "Cost: Removed one counter.", "You drew 3 cards." })
                && otherResult.Changes.SequenceEqual(new[] { "You drew 7 cards." }) && feedback.Recent.Count == 3,
            "Multiple same-poll completions retain their own cross-poll costs and exact draw counts, including a distinct nested mana execution");
        check(announcements.PendingCount == 4 && announcements.IncompleteCount == 0,
            "Ordered stack and completion transitions announce both real actions once, including actions completed between observations; mana payment stays at its source");
        Observe(settled with { Revision = settled.Revision + 1 }, 2.1);
        check(announcements.PendingCount == 4 && feedback.Recent.Count == 3 && result.Started == 2,
            "An unchanged observation cannot duplicate execution announcements, outcome records or their timing");
        var announced = new List<string>();
        for (int index = 0; index < 4; index++) announced.Add(announcements.Current(2.1 + index * 2.5)!.Id);
        check(announced.SequenceEqual(new[] { "execution:activation:5", "execution:activation:7", "execution:other:8", "execution:other:9" }),
            "Actions completed between polls retain exact cast-to-resolution order instead of promoting a historical cast ahead of an earlier resolution");

        var again = activation with { Id = "activation-again", BudgetRootId = "activation-again", Step = ExecutionStep.Complete,
            Outcome = ExecutionOutcome.Cancelled };
        var cancelled = settled with { Executions = new(1, 11, [.. settled.Executions!.Occurrences, again],
            [.. settled.Executions.Transitions, new(10, again.Id, ExecutionStep.Preparation),
                new(11, again.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Cancelled }]),
            Activity = [.. settled.Activity, Event(7, again.Id, ExecutionStep.Complete, "resolved", "Activation cancelled.")] };
        Observe(cancelled, 13);
        check(feedback.Last is { ExecutionId: "activation-again", Changes.Length: 0, Outcome: "notResolved" }
                && feedback.Recent.Single(item => item.ExecutionId == activation.Id).Changes.Length == 2,
            "A repeated activation of the same card has its own cancelled outcome and cannot inherit the previous activation's paid costs or draw");
        Observe(cancelled, 14, baseline: true);
        check(feedback.Last == null && feedback.Recent.Count == 0 && counters.Groups.Count == 0 && announcements.PendingCount == 0,
            "Reconnection baselines every execution consumer without replaying retained costs, casts or completions");
        var gap = cancelled with { Executions = new(50, 51, cancelled.Executions!.Occurrences,
            [new(51, again.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Cancelled }]) };
        Observe(gap, 15);
        check(feedback.Last == null && counters.Groups.Count == 0 && announcements.PendingCount == 0 && announcements.IncompleteCount == 1,
            "A lost execution window establishes a visible incomplete-history baseline instead of inventing unseen steps or replaying the retained outcome");

        var joined = new ActionFeedback(); var joinedActivity = new PublicActivityCursor(); var joinedCursor = new ExecutionCursor();
        var joinedBaseline = joinedCursor.Observe(started);
        joined.Observe(joinedActivity.Observe(null, started, joinedBaseline.Transitions, joinedBaseline.Baseline), 20);
        var joinedCompletion = joinedCursor.Observe(settled);
        joined.Observe(joinedActivity.Observe(started, settled, joinedCompletion.Transitions), 21);
        check(joined.Recent.Single(item => item.ExecutionId == activation.Id) is { HistoryIncomplete: true } partial
                && partial.Changes.SequenceEqual(new[] { "You drew 3 cards." }),
            "Joining during an activation marks its later result incomplete and never claims to have observed the earlier counter payment");

        var missingEvents = new ActionFeedback(); var missingActivity = new PublicActivityCursor(); var missingCursor = new ExecutionCursor();
        missingCursor.Observe(initial); missingActivity.Observe(null, initial);
        var observedCost = missingCursor.Observe(started);
        missingEvents.Observe(missingActivity.Observe(initial, started, observedCost.Transitions), 30);
        var evicted = settled with { Activity = [settled.Activity[1] with { Id = 100 }] };
        var completionAfterGap = missingCursor.Observe(evicted);
        var lostActivity = missingActivity.Observe(started, evicted, completionAfterGap.Transitions);
        missingEvents.Observe(lostActivity, 31);
        check(lostActivity.HasHistoryGap && !completionAfterGap.Gap
                && missingEvents.Recent.Single(item => item.ExecutionId == activation.Id).HistoryIncomplete,
            "Committed activity loss marks partial results even when the separate execution-transition stream has no gap");

        var silent = new ActionFeedback(); var silentActivity = new PublicActivityCursor(); var silentCursor = new ExecutionCursor();
        silentCursor.Observe(initial); silentActivity.Observe(null, initial);
        var silentStart = started with { Activity = [cost with { ExecutionId = "", ExecutionStep = null }] };
        var silentPreparation = silentCursor.Observe(silentStart);
        silent.Observe(silentActivity.Observe(initial, silentStart, silentPreparation.Transitions), 40);
        var unrelatedGap = silentStart with { Activity = [Event(100, other.Id, ExecutionStep.Resolution, "draw", "You drew a card.", 1)],
            Executions = new(1, 4, [activation, other], [.. started.Executions!.Transitions,
                new(3, other.Id, ExecutionStep.Stack), new(4, other.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]) };
        var unrelatedTransitions = silentCursor.Observe(unrelatedGap);
        var unrelatedBatch = silentActivity.Observe(silentStart, unrelatedGap, unrelatedTransitions.Transitions);
        silent.Observe(unrelatedBatch, 41);
        var silentEnd = unrelatedGap with { Executions = new(1, 5,
            [activation with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }, other],
            [.. unrelatedGap.Executions!.Transitions, new(5, activation.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]) };
        var silentCompletion = silentCursor.Observe(silentEnd);
        silent.Observe(silentActivity.Observe(unrelatedGap, silentEnd, silentCompletion.Transitions), 42);
        check(unrelatedBatch.HasHistoryGap && silent.Recent.Single(item => item.ExecutionId == activation.Id)
                is { HistoryIncomplete: true, Changes.Length: 0 },
            "Activity loss marks every ongoing execution incomplete even before its first visible change or next transition");

        var declinedFeedback = new ActionFeedback(); var declinedActivity = new PublicActivityCursor();
        declinedActivity.Observe(null, initial);
        var declined = initial with { Executions = new(1, 1,
            [other with { Outcome = ExecutionOutcome.Declined }],
            [new(1, other.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Declined }]),
            Activity = [Event(1, other.Id, ExecutionStep.Resolution, "resolved", "Another spell resolved.") with { Outcome = "resolved" }] };
        declinedFeedback.Observe(declinedActivity.Observe(initial, declined, declined.Executions!.Transitions), 45);
        check(declinedFeedback.Last is { Title: "Another spell declined.", Outcome: "notResolved", Changes.Length: 0 },
            "A declined optional effect reports its actual terminal result even when the legacy engine event calls its stack cleanup a resolution");

        var held = new StackArrivalPresentation();
        var heldStack = settled with { Status = "resolving", Stack = [
            new(source.Name, "Draw three cards.", StackActionKind.ActivatedAbility, null) { Id = "old-activation-stack", ExecutionId = activation.Id },
            new("Another spell", "Draw seven cards.", StackActionKind.Spell, null) { Id = "old-spell-stack", ExecutionId = other.Id }] };
        held.Observe(initial, heldStack, 50, settled.Activity, executions: settled.Executions!.Transitions);
        var heldOrder = Enumerable.Range(0, 4).Select(index => held.Current(50.1 + index * 2.5)!.Id).ToArray();
        check(heldOrder.SequenceEqual(new[] { "execution:activation:5", "execution:activation:7", "execution:other:8", "execution:other:9" }),
            "A resolving snapshot's pinned old stack cannot promote completed casts ahead of their earlier reported resolutions");

        CheckDeferredMetadata(initial, source, check);
    }

    private static void CheckDeferredMetadata(GameSnapshot initial, Card source, Action<bool, string> check)
    {
        var occurrence = new ExecutionOccurrence("deferred-activation", "deferred-activation", ExecutionKind.Activation, ExecutionStep.Stack) {
            ActorPlayerId = initial.ViewerId, SourceCardId = source.VisualId, SourceName = source.Name };
        var early = initial with { Executions = new(1, 2, [occurrence], [new(1, occurrence.Id, ExecutionStep.Preparation),
            new(2, occurrence.Id, ExecutionStep.Stack)]) };
        var cursor = new ExecutionCursor(); var activity = new PublicActivityCursor(); var announcements = new StackArrivalPresentation();
        GameSnapshot? before = null;
        void Observe(GameSnapshot state, double now)
        {
            var execution = cursor.Observe(state);
            var events = activity.Observe(before, state, execution.Transitions, execution.Baseline, execution.Gap);
            announcements.Observe(before, state, now, events.Entries, events.HasHistoryGap,
                execution.Transitions, execution.Baseline, execution.Gap);
            before = state;
        }
        Observe(initial, 60); Observe(early, 61);
        check(announcements.PendingCount == 1 && announcements.Current(71) == null && !announcements.Ready(71),
            "An actual stack transition waits for its exact reading material instead of showing an empty uninspectable announcement or expiring unseen");
        var item = new StackItem(source.Name, "Draw a card.", StackActionKind.ActivatedAbility, null) {
            Id = "late-current-stack", ExecutionId = occurrence.Id, ControllerId = initial.ViewerId };
        var cast = new ActivityEntry(1, initial.Turn, initial.PhaseKey, "ability", initial.ViewerId,
            "You activated an ability.", source.VisualId, source.Name) {
                ExecutionId = occurrence.Id, ExecutionStep = ExecutionStep.Stack, StackId = item.Id };
        var disclosed = early with { Stack = [item], Activity = [cast] };
        Observe(disclosed, 72);
        check(announcements.PendingCount == 1 && announcements.Current(72) is { StackId: "late-current-stack", Text: "Draw a card." }
                && !announcements.Ready(73),
            "Late current-stack metadata fills the same deferred execution once, exposes its exact hover reference and starts its first readable beat without another transition");
        var resolved = disclosed with { Stack = [],
            Executions = new(1, 4, [occurrence with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }],
                [.. early.Executions!.Transitions, new(3, occurrence.Id, ExecutionStep.Resolution),
                    new(4, occurrence.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
            Activity = [cast, new(2, initial.Turn, initial.PhaseKey, "resolved", initial.ViewerId, "Ability resolved.", source.VisualId, source.Name) {
                ExecutionId = occurrence.Id, ExecutionStep = ExecutionStep.Resolution, StackId = item.Id, Detail = "Draw a card." }] };
        Observe(resolved, 73); Observe(resolved, 73.1);
        check(announcements.PendingCount == 2 && announcements.Current(73.1)?.Id == "execution:deferred-activation:2"
                && announcements.Current(74.5)?.Id == "execution:deferred-activation:4" && announcements.IncompleteCount == 0,
            "A late-readable activation keeps its full first beat, then its distinct terminal announcement, without duplicate entries or a polling timer reset");

        var unavailable = new StackArrivalPresentation();
        unavailable.Observe(initial, early, 80, executions: early.Executions.Transitions);
        unavailable.Observe(early, resolved with { Activity = [] }, 81, executions: resolved.Executions.Transitions.Skip(2).ToImmutableArray());
        check(unavailable.IncompleteCount == 1 && unavailable.PendingCount == 1 && unavailable.Current(81)?.EventKind == "resolved",
            "Evicted action reading material reports incomplete history while the actual terminal fact remains readable, instead of blocking on a missing stack announcement forever");
    }
}
