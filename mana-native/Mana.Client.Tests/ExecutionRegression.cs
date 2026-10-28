using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class ExecutionRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var state = new GameSnapshot("execution-game", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null, null);
        var root = new ExecutionOccurrence("activation-1", "activation-1", ExecutionKind.Activation, ExecutionStep.Cost) {
            ActorPlayerId = 0, SourceCardId = "source-1", SourceName = "Source" };
        var mana = new ExecutionOccurrence("mana-1", root.Id, ExecutionKind.Mana, ExecutionStep.Complete) {
            ParentId = root.Id, ActorPlayerId = 0, Outcome = ExecutionOutcome.Resolved };
        state = state with { Executions = new(1, 4, [root, mana], [new(1, root.Id, ExecutionStep.Preparation),
            new(3, mana.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }, new(4, root.Id, ExecutionStep.Cost)]) };
        var cursor = new ExecutionCursor();
        check(cursor.Observe(state) is { Baseline: true, Gap: false, Transitions.IsEmpty: true },
            "Joining a match establishes an execution baseline without replaying earlier actions");
        var advanced = state with { Executions = state.Executions! with { LastSequence = 8,
            Transitions = [new(3, mana.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }, new(7, root.Id, ExecutionStep.Cost)] } };
        var batch = cursor.Observe(advanced);
        check(!batch.Baseline && !batch.Gap && batch.Transitions.Select(item => item.Sequence).SequenceEqual([7L]),
            "Viewer-filtered sequence holes remain ordered history rather than fabricated transport gaps");
        check(cursor.Observe(advanced).Transitions.IsEmpty, "Polling the same execution history never repeats a transition");
        var connection = new TraceConnection();
        var session = new ClientSession(connection);
        session.Accept(state with { Revision = 1 }); session.Accept(advanced with { Revision = 2 });
        check(!session.Accept(state with { Revision = 1 }) && session.State?.Revision == 2,
            "An older accepted-observation candidate is ignored before its older execution cursor can manufacture a recovery failure");
        var missed = advanced with { Executions = advanced.Executions! with { FirstSequence = 20, LastSequence = 21,
            Transitions = [new(21, root.Id, ExecutionStep.Cost)] } };
        check(cursor.Observe(missed) is { Baseline: true, Gap: true, Transitions.IsEmpty: true },
            "An evicted execution range establishes a new baseline instead of inventing missed actions");
        check(cursor.Observe(missed with { Executions = missed.Executions! with { LastSequence = 22 } }, baseline: true)
                is { Baseline: true, Gap: false, Transitions.IsEmpty: true },
            "Reconnection keeps authoritative current state without replaying retained history");
        var roundtrip = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(state))!;
        ExecutionFrames.Validate(roundtrip);
        check(roundtrip.Executions?.Occurrences[1].BudgetRootId == root.Id,
            "Immutable native observation roundtrips preserve a nested mana action's exact execution and budget root");
        var trigger = new ExecutionOccurrence("trigger-1", "trigger-1", ExecutionKind.Trigger, ExecutionStep.Preparation) { ParentId = root.Id };
        ExecutionFrames.Validate(state with { Executions = state.Executions! with { Occurrences = [root, mana, trigger] } });
        check(trigger.ParentId == root.Id && trigger.BudgetRootId == trigger.Id,
            "A causally produced trigger can receive its own allowance without losing its producing execution");
        var completedRoot = root with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved };
        var copy = new ExecutionOccurrence("copied-ability", root.Id, ExecutionKind.Activation, ExecutionStep.Choice) { ParentId = root.Id };
        var choosingCopy = state with { Executions = state.Executions! with { Occurrences = [completedRoot, mana, copy], Transitions = [] },
            Decision = new("copy-input", new(), new ConfirmationPayload(new("Choose", true))) {
                ExecutionId = copy.Id, ExecutionStep = ExecutionStep.Choice } };
        ExecutionFrames.Validate(choosingCopy);
        check(ExecutionFrames.LiveBudgetRoots(choosingCopy).SetEquals([root.Id]),
            "A live copied ability can own its resolution choice after its producer completes while retaining that producer's allowance root");
        check(ExecutionFrames.LiveBudgetRoots(choosingCopy with { Decision = null, Executions = choosingCopy.Executions! with {
                Occurrences = [completedRoot, mana, copy with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }] } }).IsEmpty
            && ExecutionFrames.LiveBudgetRoots(choosingCopy with { Decision = null, Executions = choosingCopy.Executions! with {
                Occurrences = [completedRoot, mana] } }).IsEmpty,
            "A completed producing root becomes unused only after its final live child completes or leaves the retained occurrence set");
        ExecutionFrames.Validate(state with { Stack = [new("Ability", "", StackActionKind.ActivatedAbility, null) { ExecutionId = root.Id }],
            Executions = state.Executions! with { Occurrences = [root with { Step = ExecutionStep.Choice }, mana],
                Transitions = [new(3, mana.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }, new(4, root.Id, ExecutionStep.Choice)] } });
        check(true, "Stack membership and the current execution step stay separate while an ability owns a nested choice");
        Reject(state with { Executions = state.Executions! with { Occurrences = [root, mana with { BudgetRootId = "missing" }] } },
            "A missing root cannot silently renew an execution's allowance");
        Reject(state with { Executions = state.Executions! with { Occurrences = [root with { ParentId = mana.Id }, mana] } },
            "Cyclic execution ancestry is rejected before presentation or policy use");
        Reject(state with { Executions = state.Executions! with { Transitions = [new(3, root.Id, ExecutionStep.Cost), new(2, root.Id, ExecutionStep.Cost)] } },
            "Reordered transitions cannot rewrite the committed execution stream");
        Reject(state with { Executions = state.Executions! with { Transitions = [new(1, root.Id, ExecutionStep.Complete)] } },
            "A terminal execution transition must state its actual outcome");
        Reject(state with { Executions = state.Executions! with { Transitions = [new(1, root.Id, ExecutionStep.Complete) {
            Outcome = ExecutionOutcome.Cancelled }, new(2, root.Id, ExecutionStep.Cost)] } },
            "A cancelled execution cannot resume under the old occurrence identity");
        Reject(state with { Executions = state.Executions! with { Transitions = [new(4, root.Id, ExecutionStep.Complete) {
            Outcome = ExecutionOutcome.Resolved }] }, Decision = new("contradictory-current-input", new(), new ConfirmationPayload(new("OK", true))) {
                ExecutionId = root.Id, ExecutionStep = ExecutionStep.Cost } },
            "A terminal retained transition cannot contradict a current cost occurrence and manufacture a live decision for a completed execution");
        Reject(state with { Executions = state.Executions! with { Occurrences = [completedRoot, mana],
            Transitions = [new(4, root.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Cancelled }] } },
            "The latest retained terminal outcome must agree with the occurrence's current outcome");
        Reject(state with { Decision = new("input", new(), new ConfirmationPayload(new("OK", true))) {
            ExecutionId = root.Id, ExecutionStep = ExecutionStep.Choice } },
            "A current decision must name the exact current execution step");
        foreach (string wire in new[] { "2", "\"2\"", "\"Preparation\"", "\"preparation, choice\"", "\"imagined-step\"" }) {
            bool rejected = false;
            try { _ = JsonSerializer.Deserialize<ExecutionStep>(wire); } catch (JsonException) { rejected = true; }
            check(rejected, "Execution enums require exact closed protocol names rather than numeric, combined or unknown variants");
        }

        var replacement = new ExecutionOccurrence("replacement", "replacement", ExecutionKind.Rules, ExecutionStep.Complete) {
            RuleKind = ExecutionRuleKind.Replacement, RuleResult = ExecutionRuleResult.Prevented, Outcome = ExecutionOutcome.Resolved };
        var ruleState = state with { Executions = new(1, 2, [replacement], [new(2, replacement.Id, ExecutionStep.Complete) {
            Outcome = ExecutionOutcome.Resolved, RuleResult = ExecutionRuleResult.Prevented }]) };
        var rulesRoundtrip = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(ruleState))!;
        ExecutionFrames.Validate(rulesRoundtrip);
        check(rulesRoundtrip.Executions?.Occurrences[0] is { RuleKind: ExecutionRuleKind.Replacement, RuleResult: ExecutionRuleResult.Prevented },
            "A replacement's actual prevented result survives immutable observation JSON without claiming its calling spell was prevented");
        var stateBased = replacement with { Id = "state-based", BudgetRootId = "state-based", RuleKind = ExecutionRuleKind.StateBased, RuleResult = null };
        ExecutionFrames.Validate(state with { Executions = new(1, 2, [stateBased], []) });
        check(stateBased.SourceCardId == null && stateBased.ActorPlayerId == null,
            "A public state-based result can have an exact rules owner without inventing a source card or culpable player");
        ExecutionFrames.Validate(state with { Executions = new(1, 2, [replacement with { RuleResult = null }], []) });
        check(true, "A buffered replacement follow-up with no individual application result retains explicit unknown result metadata");
        Reject(ruleState with { Executions = ruleState.Executions! with { Occurrences = [replacement with { Kind = ExecutionKind.Spell }] } },
            "Rule metadata cannot reclassify an ordinary spell as a replacement execution");
        Reject(ruleState with { Executions = ruleState.Executions! with { Occurrences = [replacement with {
            RuleKind = ExecutionRuleKind.StateBased }], Transitions = [] } },
            "A state-based check cannot acquire a replacement application result");
        Reject(ruleState with { Executions = ruleState.Executions! with { Occurrences = [replacement with {
            Step = ExecutionStep.Resolution, Outcome = null }], Transitions = [] } },
            "A replacement result cannot precede its actual terminal transition");
        Reject(ruleState with { Executions = ruleState.Executions! with { Transitions = [new(2, replacement.Id, ExecutionStep.Complete) {
            Outcome = ExecutionOutcome.Resolved, RuleResult = ExecutionRuleResult.Replaced }] } },
            "A replacement occurrence and its latest transition cannot report contradictory results");
        foreach (string wire in new[] { "1", "\"StateBased\"", "\"other\"" }) {
            bool rejected = false;
            try { _ = JsonSerializer.Deserialize<ExecutionRuleKind>(wire); } catch (JsonException) { rejected = true; }
            check(rejected, "Rule kinds use exact closed wire names and cannot be guessed from unknown provider values");
        }

        void Reject(GameSnapshot invalid, string message)
        {
            bool rejected = false;
            try { ExecutionFrames.Validate(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, message);
        }
    }

    private sealed class TraceConnection : IMatchConnection
    {
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(null);
        public Task ExecuteAsync(GameCommand command) => throw new NotSupportedException();
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
