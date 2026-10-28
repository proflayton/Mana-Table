using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Table.Presentation;

namespace Mana.Table;

// Authored causality fixtures exercise the production attention owner. Physical
// input lives in PriorityContinuationChecks; ExecutionEngineJourney supplies
// an independently generated real-provider activation/resolution sequence.
internal static class PriorityAcknowledgmentChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var quiet = Quiet(); var stacked = Stack(quiet); var resolved = Resolve(stacked);
        Check("An accepted top response settles into a fresh quiet permission without asking twice", resolved, false);
        Check("Correlated resolution does not require a permission change to remain acknowledged", resolved with {
            Decision = Priority("resolved-same-token", "before-action") }, false);
        Check("An unchanged publication while the accepted pass is in flight does not ask twice", stacked with { Revision = 3 }, false);
        Check("A new spell arriving during the pass keeps its own response unanswered", Stack(resolved, "another-spell", ExecutionKind.Spell), true);
        Check("A child trigger is a new response even when the accepted action caused it", Stack(resolved, "child-trigger", ExecutionKind.Trigger, "accepted-action"), true);
        var replacement = RulesResults(stacked, 1); var nestedReplacement = RulesResults(stacked, 2);
        Check("A disclosed replacement owned by the passed stack action settles without asking for the same response twice", replacement, false);
        var zeroCost = replacement with { Executions = replacement.Executions! with {
            LastSequence = replacement.Executions!.LastSequence + 1,
            Transitions = replacement.Executions!.Transitions.SelectMany(item => item.ExecutionId == "replacement-1" && item.Step == ExecutionStep.Preparation
                ? new[] { item, new ExecutionTransition(0, item.ExecutionId, ExecutionStep.Cost) } : new[] { item })
                .Select((item, index) => item with { Sequence = replacement.Executions!.FirstSequence + index }).ToImmutableArray() } };
        Check("A completed zero-cost replacement's historical cost phase does not invent a payment or required choice", zeroCost, false);
        Check("A replacement still paying costs cannot inherit settled quiet continuation", replacement with {
            Executions = replacement.Executions! with {
                Occurrences = replacement.Executions!.Occurrences.Select(item => item.Id == "replacement-1"
                    ? item with { Step = ExecutionStep.Cost, Outcome = null } : item).ToImmutableArray(),
                Transitions = replacement.Executions!.Transitions.Select(item => item.ExecutionId == "replacement-1" && item.Step == ExecutionStep.Complete
                    ? item with { Step = ExecutionStep.Cost, Outcome = null } : item).ToImmutableArray() } }, true);
        Check("Nested disclosed replacements retain the exact passed response through every parent link", nestedReplacement, false);
        Check("A same-root rules occurrence with no explicit parent does not inherit the passed response", MutateRule(replacement,
            item => item with { ParentId = null }), true);
        Check("A descendant trigger is a new response even when it retains the same budget root", MutateRule(replacement,
            item => item with { Kind = ExecutionKind.Trigger, RuleKind = null }), true);
        Check("An independent state-based action cannot inherit the passed stack response", MutateRule(replacement,
            item => item with { ParentId = null, BudgetRootId = item.Id, RuleKind = ExecutionRuleKind.StateBased,
                SourceCardId = null, SourceName = null }), true);
        Check("An undisclosed rules source cannot authorize quiet continuation", MutateRule(replacement,
            item => item with { SourceCardId = null }), true);
        Check("Untyped legacy rules metadata remains unknown for response acknowledgment", MutateRule(replacement,
            item => item with { RuleKind = null }), true);
        Check("A hidden intermediate parent breaks nested replacement acknowledgment", MutateRule(nestedReplacement,
            item => item.Id == "replacement-1" ? item with { SourceCardId = null } : item), true);
        Check("Rules descendants cannot acknowledge a stack action hidden behind a rules label", replacement with {
            Executions = replacement.Executions! with { Transitions = replacement.Executions!.Transitions.Select(item =>
                item.ExecutionId == "replacement-1" && item.Step == ExecutionStep.Preparation ? item with { Step = ExecutionStep.Stack } : item).ToImmutableArray() } }, true);
        Check("A required choice inside a replacement interrupts even when it settles between observations", replacement with {
            Executions = replacement.Executions! with { Transitions = replacement.Executions!.Transitions.Select(item =>
                item.ExecutionId == "replacement-1" && item.Step == ExecutionStep.Preparation ? item with { Step = ExecutionStep.Choice } : item).ToImmutableArray() } }, true);
        Check("A live required replacement choice still takes attention immediately", RuleChoice(stacked), true);
        Check("A replacement activity cost cannot be mistaken for a committed resolution", replacement with {
            Activity = replacement.Activity.Select(item => item.ExecutionId == "replacement-1"
                ? item with { ExecutionStep = ExecutionStep.Cost } : item).ToImmutableArray() }, true);
        Check("A replacement cannot bypass a missing execution-history boundary", replacement with {
            Executions = replacement.Executions! with { FirstSequence = 3,
                Transitions = replacement.Executions!.Transitions.Where(item => item.Sequence >= 3).ToImmutableArray() } }, true);
        Check("A required choice retires the accepted response even when it belongs to that execution", Choice(stacked), true);
        Check("Another seat's private required choice interrupts without exposing its options", stacked with {
            Revision = 3, Decision = null, PriorityPlayerId = null, DecisionOwnerId = 1, ActionClass = "selection" }, true);
        Check("Missing historical execution data never authorizes quiet continuation", resolved with {
            Executions = null, Activity = resolved.Activity.Select(e => e with { ExecutionId = "", ExecutionStep = null }).ToImmutableArray() }, true);
        Check("A fresh token without terminal execution proof cannot acknowledge an action", resolved with {
            Activity = stacked.Activity, Executions = stacked.Executions }, true);
        Check("Unattributed public changes during resolution keep attention stopped", resolved with {
            Activity = resolved.Activity.Add(new(3, 2, "MAIN2", "life", 2, "Seat 2 lost life.", null, null)) }, true);
        Check("A cost event cannot be mistaken for the accepted resolution", resolved with {
            Activity = resolved.Activity.Add(new(3, 2, "MAIN2", "counters", 1, "A cost was paid.", null, null) {
                ExecutionId = "accepted-action", ExecutionStep = ExecutionStep.Cost }) }, true);
        Check("A changed interruption fence remains authoritative even with correlated resolution history", resolved with { StackInterruptionVersion = 2 }, true);
        Check("Omitted interruption metadata stays conservative", resolved with { StackInterruptionVersion = null }, true);
        Check("Truncated public activity cannot hide an unrelated change behind retained execution history", resolved with {
            Activity = [resolved.Activity.Last() with { Id = 3 }] }, true);
        Check("Reusing an execution ID with a different historical actor cannot inherit acknowledgment", resolved with {
            Executions = resolved.Executions! with { Occurrences = resolved.Executions!.Occurrences.Select(e => e with { ActorPlayerId = 2 }).ToImmutableArray() } }, true);
        Check("Reusing an execution ID for a different action kind cannot inherit acknowledgment", resolved with {
            Executions = resolved.Executions! with { Occurrences = resolved.Executions!.Occurrences.Select(e => e with { Kind = ExecutionKind.Trigger }).ToImmutableArray() } }, true);
        var terminal = resolved.Executions!.Transitions.Last();
        Check("A retained occurrence after a history gap cannot renew consent", resolved with {
            Executions = resolved.Executions with { FirstSequence = terminal.Sequence, Transitions = [terminal] } }, true);
        var older = new StackItem("Older spell", "An older pending effect.", StackActionKind.Spell, null) { Id = "older-stack", ExecutionId = "older-action", ControllerId = 2 };
        var olderExecution = new ExecutionOccurrence("older-action", "older-action", ExecutionKind.Spell, ExecutionStep.Stack) { ActorPlayerId = 2 };
        var two = stacked with { Stack = stacked.Stack.Add(older), Executions = stacked.Executions! with {
            Occurrences = stacked.Executions!.Occurrences.Add(olderExecution) } };
        var one = Resolve(two) with { Stack = [older] };
        CheckFrom("Passing the top item does not acknowledge an older remaining stack item", two, one, true);

        var unacknowledged = new PriorityAttention(); unacknowledged.Observe(quiet); unacknowledged.Observe(stacked); unacknowledged.Observe(resolved);
        check(unacknowledged.NeedsResponse, "A spell disappearing before the player responds does not silently acknowledge it");
        var stale = new PriorityAttention(); stale.Observe(quiet); stale.Observe(stacked);
        stale.Submitted(stacked, new PassPriority(stacked.Id, "obsolete-decision")); stale.Observe(resolved);
        check(stale.NeedsResponse, "A stale or rejected command cannot manufacture a response acknowledgment");
        var mode = new PriorityAttention(); mode.Observe(quiet); mode.Observe(stacked); mode.Resume(); mode.Observe(resolved);
        check(mode.NeedsResponse, "Changing a local playback mode does not manufacture a passed stack occurrence");
        var after = new PriorityAttention(); after.Observe(quiet); after.Observe(stacked); after.Observe(resolved);
        after.Submitted(resolved, new PassPriority(resolved.Id, resolved.Decision!.Id)); after.Observe(resolved with { Revision = 5 });
        check(!after.NeedsResponse, "Passing the current empty window after resolution acknowledges only that current window");
        var deferred = new PriorityAttention(); deferred.Observe(quiet); deferred.Observe(stacked);
        deferred.Submitted(stacked, new PassPriority(stacked.Id, stacked.Decision!.Id));
        deferred.Observe(resolved with { Status = "resolving", Decision = null, DecisionOwnerId = null, PriorityPlayerId = null, ActionClass = "resolving" });
        deferred.Observe(resolved with { Revision = 5 });
        check(!deferred.NeedsResponse, "Completion published before the fresh local priority token retains exact acknowledgment across both observations");

        void Check(string label, GameSnapshot next, bool expected) => CheckFrom(label, stacked, next, expected);
        void CheckFrom(string label, GameSnapshot before, GameSnapshot next, bool expected)
        {
            ExecutionFrames.Validate(before); ExecutionFrames.Validate(next);
            var owner = new PriorityAttention(); owner.Observe(quiet); owner.Observe(before);
            check(owner.NeedsResponse, "A new observed stack action requests attention before the bounded pass");
            owner.Submitted(before, new PassPriority(before.Id, before.Decision!.Id));
            owner.Observe(next);
            check(owner.NeedsResponse == expected, label);
        }
    }

    internal static GameSnapshot Quiet()
    {
        var instant = new Card { VisualId = "response-instant", CombatId = "response-instant", Key = "instant-current",
            Name = "Lightning Bolt", Type = "Instant", ManaCost = "{R}", Text = "Lightning Bolt deals 3 damage to any target.", Selectable = true };
        var state = PriorityAbilitySmoke.Example();
        return state with { Id = "acknowledged-priority", Turn = 2, ActivePlayerId = 1, Phase = "Second main phase", PhaseKey = "MAIN2",
            ActionClass = "priority", PriorityPlayerId = state.ViewerId, DecisionOwnerId = state.ViewerId, Revision = 1,
            Stack = [], StackInterruptionVersion = 0, Activity = [], Executions = new(1, 0, [], []), Decision = Priority("quiet-before", "before-action"),
            Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                LandPlaysUsed = 1, Mana = player.Mana.SetItem("R", 1),
                Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 1, Cards = [instant] }
                    : zone.Name == "Battlefield" ? zone with { Count = 0, Cards = [] } : zone).ToImmutableArray() }).ToImmutableArray() };
    }

    internal static GameSnapshot Stack(GameSnapshot state, string id = "accepted-action", ExecutionKind kind = ExecutionKind.Spell, string? parent = null)
    {
        var frame = state.Executions ?? new(1, 0, [], []); long sequence = frame.LastSequence + 1;
        var occurrence = new ExecutionOccurrence(id, id, kind, ExecutionStep.Stack) { ParentId = parent, ActorPlayerId = 1, SourceName = "Observed pending effect" };
        return state with { Revision = state.Revision + 1, StackInterruptionVersion = (state.StackInterruptionVersion ?? 0) + 1,
            Stack = [new("Observed pending effect", "An effect is pending.", kind == ExecutionKind.Trigger ? StackActionKind.TriggeredAbility : StackActionKind.Spell, null) {
                Id = "stack-" + id, ExecutionId = id, ControllerId = 1 }], Decision = Priority("response-" + id, ""),
            Activity = state.Activity.Add(new(state.Activity.Select(e => e.Id).DefaultIfEmpty().Max() + 1, state.Turn, state.PhaseKey,
                kind == ExecutionKind.Spell ? "cast" : "ability", 1, "A new action was announced.", null, "Observed pending effect") {
                ExecutionId = id, ExecutionStep = ExecutionStep.Stack, StackId = "stack-" + id }),
            Executions = new(1, sequence, frame.Occurrences.Add(occurrence), frame.Transitions.Add(new(sequence, id, ExecutionStep.Stack))) };
    }

    internal static GameSnapshot Resolve(GameSnapshot state)
    {
        var frame = state.Executions!; var top = state.Stack[0]; string id = top.ExecutionId;
        return state with { Revision = state.Revision + 1, Stack = [], Decision = Priority("quiet-after", "after-accepted-resolution"),
            Activity = state.Activity.Add(new(state.Activity.Select(e => e.Id).DefaultIfEmpty().Max() + 1, state.Turn, state.PhaseKey,
                "resolved", 1, "The accepted action resolved.", null, "Observed pending effect") {
                ExecutionId = id, ExecutionStep = ExecutionStep.Resolution, StackId = top.Id, Outcome = "resolved" }),
            Executions = frame with { LastSequence = frame.LastSequence + 2,
                Occurrences = frame.Occurrences.Select(e => e.Id == id ? e with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved } : e).ToImmutableArray(),
                Transitions = frame.Transitions.Add(new(frame.LastSequence + 1, id, ExecutionStep.Resolution))
                    .Add(new(frame.LastSequence + 2, id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }) } };
    }

    private static GameSnapshot Choice(GameSnapshot state)
    {
        var frame = state.Executions!; string id = state.Stack[0].ExecutionId;
        return state with { Revision = state.Revision + 1, Stack = [], ActionClass = "selection", PriorityPlayerId = null,
            Decision = new("number-required", new(), new NumberPayload("1")) { ExecutionId = id, ExecutionStep = ExecutionStep.Choice },
            Executions = frame with { LastSequence = frame.LastSequence + 1,
                Occurrences = frame.Occurrences.Select(e => e.Id == id ? e with { Step = ExecutionStep.Choice } : e).ToImmutableArray(),
                Transitions = frame.Transitions.Add(new(frame.LastSequence + 1, id, ExecutionStep.Choice)) } };
    }

    private static GameSnapshot RulesResults(GameSnapshot state, int depth)
    {
        var resolved = Resolve(state); var frame = state.Executions!; string root = state.Stack[0].ExecutionId;
        var rootOccurrence = resolved.Executions!.Occurrences.Single(item => item.Id == root);
        var occurrences = resolved.Executions.Occurrences.ToBuilder(); var transitions = frame.Transitions.ToBuilder();
        long sequence = frame.LastSequence;
        transitions.Add(new(++sequence, root, ExecutionStep.Resolution));
        var activity = state.Activity.ToBuilder(); long eventId = activity.Select(item => item.Id).DefaultIfEmpty().Max();
        string parent = root;
        for (int index = 1; index <= depth; index++) {
            string id = "replacement-" + index;
            occurrences.Add(new(id, rootOccurrence.BudgetRootId, ExecutionKind.Rules, ExecutionStep.Complete) {
                ParentId = parent, ActorPlayerId = 1, SourceCardId = "disclosed-rules-source-" + index,
                SourceName = "Disclosed replacement", RuleKind = ExecutionRuleKind.Replacement, Outcome = ExecutionOutcome.Resolved });
            transitions.Add(new(++sequence, id, ExecutionStep.Preparation));
            transitions.Add(new(++sequence, id, ExecutionStep.Resolution));
            activity.Add(new(++eventId, state.Turn, state.PhaseKey, "counters", state.ViewerId, "Replacement changed the actual result.", null, null) {
                ExecutionId = id, ExecutionStep = ExecutionStep.Resolution, Change = new("counters", "+1/+1", 1, 3) });
            parent = id;
        }
        for (int index = depth; index >= 1; index--)
            transitions.Add(new(++sequence, "replacement-" + index, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved });
        transitions.Add(new(++sequence, root, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved });
        activity.Add(resolved.Activity.Last() with { Id = ++eventId });
        return resolved with { Activity = activity.ToImmutable(), Executions = new(frame.FirstSequence, sequence, occurrences.ToImmutable(), transitions.ToImmutable()) };
    }

    private static GameSnapshot MutateRule(GameSnapshot state, Func<ExecutionOccurrence, ExecutionOccurrence> change)
        => state with { Executions = state.Executions! with { Occurrences = state.Executions!.Occurrences
            .Select(item => item.Kind == ExecutionKind.Rules ? change(item) : item).ToImmutableArray() } };

    private static GameSnapshot RuleChoice(GameSnapshot state)
    {
        var frame = state.Executions!; string root = state.Stack[0].ExecutionId; long sequence = frame.LastSequence;
        var rule = new ExecutionOccurrence("replacement-choice", root, ExecutionKind.Rules, ExecutionStep.Choice) {
            ParentId = root, ActorPlayerId = 1, SourceCardId = "disclosed-rules-source", RuleKind = ExecutionRuleKind.Replacement };
        return state with { Revision = state.Revision + 1, Stack = [], ActionClass = "selection", PriorityPlayerId = null,
            Decision = new("replacement-number", new(), new NumberPayload("1")) { ExecutionId = rule.Id, ExecutionStep = ExecutionStep.Choice },
            Executions = frame with { LastSequence = sequence + 3,
                Occurrences = frame.Occurrences.Select(item => item.Id == root ? item with { Step = ExecutionStep.Resolution } : item).Append(rule).ToImmutableArray(),
                Transitions = frame.Transitions.Add(new(++sequence, root, ExecutionStep.Resolution))
                    .Add(new(++sequence, rule.Id, ExecutionStep.Preparation)).Add(new(++sequence, rule.Id, ExecutionStep.Choice)) } };
    }

    private static Decision Priority(string id, string token) => new(id, new(),
        new PriorityPayload(new("Pass once", true), CanAutoPass: false, QuietYieldToken: token, Abilities: []));
}
