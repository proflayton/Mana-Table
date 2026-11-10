using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.AI;
using Mana.Contracts;

internal static class AiRouteRetryRegression
{
    private const string Original = "first-disclosure", Renewed = "renewed-disclosure", Stable = "provider-occurrence-route";
    private static readonly DecisionReply Enabled = new("Continue", true), Disabled = new("Continue", false);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var session = Session();
        var original = Priority(Original, revision: 1);
        var started = await Choose(session, Frame(original, Stable));
        check(started?.Command is UseOfferedAction, "An unattempted provider-bound route can begin normally");
        session.Report(started!, AiExecutionReceipt.Accepted);

        var payment = original with { Revision = 2, BoardRevision = 2, PriorityPlayerId = null, ActionClass = "mana",
            Decision = new("actual-payment", new(), new PaymentPayload(Disabled, Enabled)) };
        var paymentFrame = new AiDecisionFrame(payment, domain: new(1, ActionDomainCoverage.Complete, [], []),
            semantics: new() { UnpaidMana = ["{2}", "{R}"] });
        var cancelled = await Choose(session, paymentFrame);
        check(cancelled?.Command is CancelPayment, "The actual payment continuation declines an unfunded bill rather than fabricating a paid result");
        session.Report(cancelled!, AiExecutionReceipt.Accepted);

        var renewed = Priority(Renewed, revision: 3);
        var guarded = await Choose(session, Frame(renewed, Stable));
        check(guarded?.Command is PassPriority && session.History.AttemptedRoutes.Count == 1,
            "A cancelled route stays guarded when its source disclosure and opaque offer change but the provider's rules-occurrence route remains the same");
        session.Report(guarded!, AiExecutionReceipt.Accepted);

        var otherCopy = Priority("distinct-physical-copy", revision: 4);
        var distinct = await Choose(session, Frame(otherCopy, "other-provider-occurrence-route"));
        check(distinct?.Command is UseOfferedAction,
            "A distinct physical copy with its own provider-bound stable route is not suppressed by another occurrence's retry guard");
        session.Report(distinct!, AiExecutionReceipt.Accepted);

        var otherRoute = Priority(Renewed, revision: 5);
        var alternative = await Choose(session, Frame(otherRoute, "different-route-on-renewed-source"));
        check(alternative?.Command is UseOfferedAction,
            "A genuinely different offered route on the same disclosed source remains available");
        session.Report(alternative!, AiExecutionReceipt.Accepted);

        var nextTurn = Priority("later-disclosure", revision: 6, turn: 9);
        var later = await Choose(session, Frame(nextTurn, Stable));
        check(later?.Command is UseOfferedAction && session.History.AttemptedRoutes.IsEmpty,
            "A new turn releases the prior turn's retry guard without permanently suppressing the provider route");

        using var shortHistory = Session(historyCapacity: 2);
        var first = await Choose(shortHistory, Frame(original, Stable));
        shortHistory.Report(first!, AiExecutionReceipt.Accepted);
        for (int revision = 2; revision <= 4; revision++) {
            var acknowledgment = original with { Revision = revision, BoardRevision = revision,
                Decision = new("required-ack-" + revision, new(), new ConfirmationPayload(Enabled)) };
            var answer = await Choose(shortHistory, new(acknowledgment));
            shortHistory.Report(answer!, AiExecutionReceipt.Accepted);
        }
        var afterEviction = await Choose(shortHistory, Frame(Priority(Renewed, revision: 5), Stable));
        check(afterEviction?.Command is PassPriority && shortHistory.History.AttemptedRoutes.Count == 1
                && shortHistory.History.Decisions.All(record => record.Command != nameof(UseOfferedAction)),
            "Evicting diagnostic records cannot reopen a stable route through a renewed source disclosure");

        var legacy = new AiSeatHistory([], []) { AttemptedRoutes = [new(8, Original, "")] };
        check(legacy.HasAttemptedRoute(8, Original, "") && !legacy.HasAttemptedRoute(8, Renewed, "")
                && !legacy.HasAttemptedRoute(9, Original, "") && !legacy.HasAttemptedRoute(8, Renewed, Stable),
            "Absent stable identity preserves only the original exact-source guard and cannot suppress unrelated sources, known routes or later turns");
        await RenewableActivations(check);
        await GuardedActivations(check);
        await ReplayAndRetention(check);
        await ResolvingHostProgress(check);
    }

    private static async Task RenewableActivations(Action<bool, string> check)
    {
        using var session = Session();
        var initial = Activation(1);
        var first = await Choose(session, ActivationFrame(initial));
        var productive = Cycle(initial, 2);
        // The provider may publish all effects before its command ACK arrives.
        session.ObserveAutomaticProgress(productive);
        check(session.TakeCommand() == null && session.History.RouteProgress.Single().Receipt == null,
            "Actual cost and resolution facts observed before the command acknowledgement cannot submit another action");
        session.Report(first!, AiExecutionReceipt.Accepted);
        var ready = Ready(productive, 3);
        check(!session.History.HasAttemptedRoute(ready, Original, ActivationFrame(ready).Domain.Actions.Single()),
            "A repeatable tap activation renews only after its exact provider route resolves a real effect and its source untaps");
        var renewed = await Choose(session, ActivationFrame(ready));
        check(renewed?.Command is UseOfferedAction && session.History.RouteProgress.Single().Execution == null,
            "Taking a renewed activation consumes that execution's witness immediately");
        session.Report(renewed!, AiExecutionReceipt.Accepted);
        var secondCycle = Cycle(ready, 4, cycle: 2);
        session.ObserveAutomaticProgress(secondCycle);
        var twice = await Choose(session, ActivationFrame(Ready(secondCycle, 5)));
        check(twice?.Command is UseOfferedAction && session.History.AttemptedRoutes.Count == 1
                && session.History.RouteProgress.Length == 1,
            "A second actual execution and resource cycle renews the same bounded logical route without accumulating generations");
        session.Report(twice!, AiExecutionReceipt.Accepted);
        var unchanged = await Choose(session, ActivationFrame(Ready(secondCycle, 6)));
        check(unchanged?.Command is PassPriority,
            "Old cost, effect and untap facts cannot authorize a second renewal after being consumed");

        using var unfinished = Session();
        var pending = await Choose(unfinished, ActivationFrame(initial));
        unfinished.Report(pending!, AiExecutionReceipt.Accepted);
        unfinished.ObserveAutomaticProgress(Cycle(initial, 2, terminal: false));
        check(unfinished.History.HasAttemptedRoute(Ready(productive, 3), Original, ActivationFrame(ready).Domain.Actions.Single()),
            "An untap and real effect do not renew an activation before its execution has a resolved terminal outcome");
        unfinished.ObserveAutomaticProgress(productive with { Revision = 3, BoardRevision = 3 });
        check((await Choose(unfinished, ActivationFrame(Ready(productive, 4))))?.Command is UseOfferedAction,
            "An untap that occurred after the productive effect but before Complete remains valid when that execution later resolves");
        foreach (var effect in new[] { ActionEffectKind.Draw, ActionEffectKind.LifeGain }) {
            using var beneficiary = Session();
            var frame = ActivationFrame(initial, effect);
            var started = await Choose(beneficiary, frame); beneficiary.Report(started!, AiExecutionReceipt.Accepted);
            var actual = productive with { Activity = productive.Activity.Select(entry => entry.Kind != "damage" ? entry
                : entry with { Kind = effect == ActionEffectKind.Draw ? "draw" : "life", PlayerId = 0,
                    Impact = effect == ActionEffectKind.Draw ? null : new("life", 1, PlayerId: 0, Before: 40, After: 41) }).ToImmutableArray() };
            beneficiary.ObserveAutomaticProgress(actual);
            check((await Choose(beneficiary, ActivationFrame(Ready(actual, 3), effect)))?.Command is UseOfferedAction,
                "A provider-bound tap route can renew from its actual own " + effect + " result without damage or prose inference");
        }
    }

    private static async Task GuardedActivations(Action<bool, string> check)
    {
        var initial = Activation(1);
        var cycle = Cycle(initial, 2);
        foreach (var failure in new[] { "metadata", "missing-route", "wrong-route", "wrong-source", "wrong-actor", "cancelled", "countered", "failed",
            "no-effect", "wrong-effect", "wrong-effect-kind", "changed-offer-effect", "wrong-cost-root", "missing-preparation", "ambiguous", "execution-trim", "activity-trim", "bound-frame-trim", "unpayable", "still-tapped", "alias", "restoration", "late-root", "changed-kind", "changed-parent", "changed-budget-root" }) {
            using var session = Session();
            var first = await Choose(session, ActivationFrame(initial, failure == "wrong-effect-kind" ? ActionEffectKind.Draw : ActionEffectKind.Damage));
            session.Report(first!, AiExecutionReceipt.Accepted);
            var observed = cycle;
            var root = cycle.Executions!.Occurrences.Single();
            observed = failure switch {
                "metadata" => Ready(initial, 2),
                "missing-route" => cycle with { Executions = cycle.Executions with { Occurrences = [root with { StableRouteId = null }] } },
                "wrong-route" => cycle with { Executions = cycle.Executions with { Occurrences = [root with { StableRouteId = "another-ability" }] } },
                "wrong-source" => cycle with { Executions = cycle.Executions with { Occurrences = [root with { SourceCardId = "another-source" }] } },
                "wrong-actor" => cycle with { Executions = cycle.Executions with { Occurrences = [root with { ActorPlayerId = 1 }] } },
                "cancelled" => Outcome(cycle, ExecutionOutcome.Cancelled),
                "countered" => Outcome(cycle, ExecutionOutcome.Countered),
                "failed" => Outcome(cycle, ExecutionOutcome.Failed),
                "no-effect" => cycle with { Activity = cycle.Activity.Where(entry => entry.Kind != "damage").ToImmutableArray() },
                "wrong-effect" => cycle with { Activity = cycle.Activity.Select(entry => entry.Impact == null ? entry
                    : entry with { Impact = entry.Impact with { SourceCardId = "another-source" } }).ToImmutableArray() },
                "wrong-cost-root" => WrongCostRoot(cycle),
                "missing-preparation" => cycle with { Executions = cycle.Executions with {
                    Transitions = cycle.Executions.Transitions.Where(item => item.Step != ExecutionStep.Preparation).ToImmutableArray() } },
                "execution-trim" => cycle with { Executions = cycle.Executions with { FirstSequence = 2,
                    Transitions = cycle.Executions.Transitions.Where(item => item.Sequence >= 2).ToImmutableArray() } },
                "activity-trim" => cycle with { Activity = cycle.Activity.Skip(1).ToImmutableArray() },
                "ambiguous" => Ambiguous(cycle),
                "restoration" => cycle with { Undo = new(1, 1, null, new(1, "restored-action", root.Id, 0)) },
                _ => cycle
            };
            if (failure == "late-root") session.ObserveAutomaticProgress(Ready(initial, 2));
            if (failure == "late-root") observed = observed with { Revision = 3, BoardRevision = 3 };
            bool changedRoot = failure is "changed-kind" or "changed-parent" or "changed-budget-root";
            if (changedRoot || failure == "bound-frame-trim") {
                session.ObserveAutomaticProgress(Cycle(initial, 2, terminal: false));
                observed = (changedRoot ? ChangedRoot(cycle, failure) : cycle with { Executions = new(5, 4, [], []), Activity = [] })
                    with { Revision = 3, BoardRevision = 3 };
            }
            session.ObserveAutomaticProgress(observed);
            var ready = Ready(observed, failure == "late-root" || changedRoot || failure == "bound-frame-trim" ? 4 : 3,
                failure == "alias" ? Renewed : Original);
            var frame = ActivationFrame(ready);
            if (failure is "wrong-effect-kind" or "changed-offer-effect") frame = ActivationFrame(ready, ActionEffectKind.Draw);
            if (failure == "unpayable") frame = new(ready, domain: frame.Domain with {
                Actions = [frame.Domain.Actions.Single() with { Affordability = ActionAffordability.Unavailable }] });
            if (failure == "still-tapped") {
                ready = ready with { Players = ready.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = [new("Battlefield", 1, [player.Zone("Battlefield").Cards.Single() with { Tapped = true }], null)] }).ToImmutableArray() };
                frame = ActivationFrame(ready);
            }
            check((await Choose(session, frame))?.Command is PassPriority && session.History.AttemptedRoutes.Count == 1,
                "Renewable activation stays guarded for " + failure + " instead of inferring productive execution from unrelated state");
        }
        foreach (var receipt in new[] { AiExecutionReceipt.Rejected, AiExecutionReceipt.Unknown }) {
            using var session = Session();
            var first = await Choose(session, ActivationFrame(initial));
            session.ObserveAutomaticProgress(cycle);
            session.Report(first!, receipt);
            var ready = Ready(cycle, 3);
            check(session.History.HasAttemptedRoute(ready, Original, ActivationFrame(ready).Domain.Actions.Single()),
                "Observed effects cannot turn a " + receipt + " command acknowledgement into permission to retry");
        }
        using var additionalCost = Session();
        var unsupported = ActivationFrame(initial);
        unsupported = new(initial, domain: unsupported.Domain with { Actions = [unsupported.Domain.Actions.Single() with {
            Costs = [new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Life, 1, false, "")] }] });
        var costly = await Choose(additionalCost, unsupported);
        additionalCost.Report(costly!, AiExecutionReceipt.Accepted);
        additionalCost.ObserveAutomaticProgress(cycle);
        check((await Choose(additionalCost, ActivationFrame(Ready(cycle, 3))))?.Command is PassPriority,
            "An activation with additional unmodeled resource costs retains the conservative guard");

        using var atomic = Session();
        var handoff = await Choose(atomic, ActivationFrame(initial)); atomic.Report(handoff!, AiExecutionReceipt.Accepted);
        bool malformed = false;
        try { atomic.ObserveAutomaticProgress(cycle with { Revision = 50, BoardRevision = 50,
            Activity = [cycle.Activity[0] with { Change = new("state", "tapped", 2, 1) }] }); }
        catch (ArgumentException) { malformed = true; }
        atomic.ObserveAutomaticProgress(cycle);
        check(malformed && (await Choose(atomic, ActivationFrame(Ready(cycle, 3))))?.Command is UseOfferedAction,
            "Malformed automatic progress is rejected before advancing the session's observation or history watermark");
    }

    private static async Task ReplayAndRetention(Action<bool, string> check)
    {
        using var session = Session(historyCapacity: 2);
        var initial = Activation(1);
        var first = await Choose(session, ActivationFrame(initial));
        session.Report(first!, AiExecutionReceipt.Accepted);
        var cycle = Cycle(initial, 2);
        session.ObserveAutomaticProgress(cycle);
        for (int revision = 3; revision <= 5; revision++) {
            var confirmation = Ready(cycle, revision) with { ActionClass = "confirm", PriorityPlayerId = null,
                Decision = new("ack-" + revision, new(), new ConfirmationPayload(Enabled)) };
            var reply = await Choose(session, new(confirmation));
            session.Report(reply!, AiExecutionReceipt.Accepted);
        }
        var ready = Ready(cycle, 6) with { Activity = [] };
        var frame = ActivationFrame(ready);
        var history = session.History;
        check(history.Decisions.All(entry => entry.Command != nameof(UseOfferedAction))
                && !history.HasAttemptedRoute(ready, Original, frame.Domain.Actions.Single()),
            "Complete execution/resource evidence survives eviction of its diagnostic command and activity records");
        var replay = new AiDecisionReplayCase(1, "renewable-route", "casual-commander.v3", ready, frame.Domain, frame.Semantics, history, 17);
        var serialized = JsonSerializer.Deserialize<AiDecisionReplayCase>(JsonSerializer.Serialize(replay))!;
        check((await AiDecisionReplay.EvaluateAsync(serialized, new CasualCommanderPolicy())).Proposal is UseOfferedAction,
            "Private Format 1 replay roundtrips the bounded causal witness and proposes only the current offered action");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(replay))!.AsObject();
        legacy["History"]!.AsObject().Remove("RouteProgress");
        check((await AiDecisionReplay.EvaluateAsync(JsonSerializer.Deserialize<AiDecisionReplayCase>(legacy.ToJsonString())!, new CasualCommanderPolicy())).Proposal is PassPriority,
            "An older Format 1 replay without optional execution progress remains readable and conservatively guarded");
        var witness = history.RouteProgress.Single();
        foreach (var invalid in new[] {
            history with { RouteProgress = [witness with { PreparationSequence = witness.ExecutionAnchor }] },
            history with { RouteProgress = [witness with { Execution = witness.Execution! with { StableRouteId = "wrong-route" } }] },
            history with { RouteProgress = [witness with { Execution = witness.Execution! with { RuleKind = ExecutionRuleKind.Replacement,
                RuleResult = ExecutionRuleResult.Prevented } }] },
            history with { RouteProgress = [witness with { Effect = witness.Effect! with { ExecutionId = "wrong-root" } }] },
            history with { RouteProgress = [witness with { Untap = witness.Untap! with { Id = witness.Cost!.Id } }] },
            history with { RouteProgress = [witness with { Untap = witness.Untap! with { Id = 1000 } }] },
            history with { RouteProgress = [witness with { Untap = witness.Untap! with { ExecutionId = "bad\nidentity" } }] },
            history with { RouteProgress = [witness with { Route = null! }] },
            history with { ImpactEvents = default(ImmutableArray<AiObservedEvent>) },
            history with { ImpactEvents = [null!] },
            history with { AttemptedRoutes = history.AttemptedRoutes.Add(new(8, Renewed, Stable)) },
            history with { AttemptedRoutes = Enumerable.Range(0, 4097).Select(index => new AiTurnRoute(8, "source-" + index, "route-" + index)).ToImmutableHashSet() }
        }) {
            bool rejected = false;
            try { await AiDecisionReplay.EvaluateAsync(replay with { History = invalid }, new CasualCommanderPolicy()); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Replay rejects contradictory, duplicate or unbounded route facts rather than trusting a renewal flag");
        }
        var aliasPolicy = new OfferedPolicy();
        using var aliases = new AiSeatSession(initial.Id, 0, aliasPolicy, 17, new(TimeSpan.FromSeconds(5), 1024));
        var original = await Choose(aliases, ActivationFrame(initial)); aliases.Report(original!, AiExecutionReceipt.Accepted);
        var alias = await Choose(aliases, ActivationFrame(Ready(initial, 2, Renewed))); aliases.Report(alias!, AiExecutionReceipt.Accepted);
        check(aliases.History.AttemptedRoutes.Count == 1 && aliases.History.RouteProgress.Length == 1,
            "Repeated submissions with fresh disclosure aliases still occupy only one stable logical route entry");
    }

    private static async Task ResolvingHostProgress(Action<bool, string> check)
    {
        var initial = Activation(1);
        var initialFrame = ActivationFrame(initial);
        var provider = new ProgressProvider { Observation = new(0, initial, initialFrame.Domain, initialFrame.Semantics) };
        await using var host = new AiTableHost(provider, _ => new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(5), 1024));
        await host.StepAsync();
        var cycle = Cycle(initial, 2);
        provider.Observation = new(0, cycle, DecisionActionDomain.None, new());
        await host.StepAsync();
        var ready = Ready(cycle, 3) with { Activity = [], Executions = new(5, 4, [], []) };
        var readyFrame = ActivationFrame(ready);
        provider.Observation = new(0, ready, readyFrame.Domain, readyFrame.Semantics);
        await host.StepAsync();
        check(provider.Commands is [UseOfferedAction, UseOfferedAction] && host.LastFault == null,
            "The production AI host observes a complete resource cycle in a resolving frame even when the next owned prompt retains none of those facts");
    }

    private static GameSnapshot Activation(long revision)
    {
        var state = Priority(Original, revision);
        var card = state.Viewer!.Zone("Hand").Cards.Single() with { Type = "Artifact", Tapped = false };
        return state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = [new("Hand", 0, [], null), new("Battlefield", 1, [card], null)] }).ToImmutableArray(), Executions = new(1, 0, [], []) };
    }
    private static AiDecisionFrame ActivationFrame(GameSnapshot state, ActionEffectKind effect = ActionEffectKind.Damage)
    {
        var card = state.Viewer!.Zone("Battlefield").Cards.Single();
        string id = "activate-" + state.Revision;
        var action = new ActionSemantics(id, effect, [effect],
            [new(ActionCostKind.Tap, 1, true, "")], ActionAffordability.Payable) { StableId = Stable, Repeatable = true };
        return new(state, domain: new(state.Revision, ActionDomainCoverage.Complete,
            [new(state.Id, state.Decision!.Id, card.Key, card.VisualId, [new(id, "activate", "Untrusted description")])], [action]));
    }
    private static GameSnapshot Ready(GameSnapshot state, long revision, string source = Original) => state with {
        Status = "playing", Revision = revision, BoardRevision = revision, PriorityPlayerId = 0, DecisionOwnerId = 0,
        Decision = new("renewable-priority-" + revision, new(), new PriorityPayload(Enabled)),
        Players = state.Players.Select(player => player.Id != 0 ? player : player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone
            : zone with { Cards = zone.Cards.Select(card => card with { VisualId = source, CombatId = source, Key = "key-" + revision }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray()
    };
    private static GameSnapshot Cycle(GameSnapshot state, long revision, int cycle = 1, bool terminal = true)
    {
        string root = "actual-activation-" + cycle;
        var execution = new ExecutionOccurrence(root, root, ExecutionKind.Activation, terminal ? ExecutionStep.Complete : ExecutionStep.Resolution) {
            StableRouteId = Stable, ActorPlayerId = 0, SourceCardId = Original, Outcome = terminal ? ExecutionOutcome.Resolved : null };
        long sequence = (cycle - 1) * 4, activity = (cycle - 1) * 3;
        ImmutableArray<ExecutionTransition> transitions = [new(sequence + 1, root, ExecutionStep.Preparation),
            new(sequence + 2, root, ExecutionStep.Cost), new(sequence + 3, root, ExecutionStep.Resolution)];
        if (terminal) transitions = transitions.Add(new(sequence + 4, root, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved });
        return state with { Status = "resolving", Revision = revision, BoardRevision = revision, PriorityPlayerId = null, DecisionOwnerId = null, Decision = null,
            Executions = new(1, sequence + (terminal ? 4 : 3), [execution], transitions), Activity = [
                new(activity + 1, 8, "MAIN1", "tapped", 0, "Cost", Original, null) {
                    Change = new("state", "tapped", 0, 1), ExecutionId = root, ExecutionStep = ExecutionStep.Cost },
                new(activity + 2, 8, "MAIN1", "damage", 1, "Committed effect", null, null) {
                    Impact = new("damage", 1, PlayerId: 1, SourceCardId: Original, SourceControllerId: 0, RecipientControllerId: 1),
                    ExecutionId = root, ExecutionStep = ExecutionStep.Resolution },
                new(activity + 3, 8, "MAIN1", "tapped", 0, "Resource renewed", Original, null) {
                    Change = new("state", "tapped", 1, 0), ExecutionId = root, ExecutionStep = ExecutionStep.Resolution } ] };
    }
    private static GameSnapshot Outcome(GameSnapshot state, ExecutionOutcome outcome) => state with { Executions = state.Executions! with {
        Occurrences = [state.Executions.Occurrences.Single() with { Outcome = outcome }],
        Transitions = state.Executions.Transitions.Select(item => item.Step != ExecutionStep.Complete ? item : item with { Outcome = outcome }).ToImmutableArray() } };
    private static GameSnapshot Ambiguous(GameSnapshot state)
    {
        const string other = "another-fresh-root";
        return state with { Executions = state.Executions! with { LastSequence = 6,
            Occurrences = state.Executions.Occurrences.Add(state.Executions.Occurrences.Single() with { Id = other, BudgetRootId = other }),
            Transitions = state.Executions.Transitions.Add(new(5, other, ExecutionStep.Preparation))
                .Add(new(6, other, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }) } };
    }
    private static GameSnapshot WrongCostRoot(GameSnapshot state)
    {
        var ambiguous = Ambiguous(state);
        string other = ambiguous.Executions!.Occurrences.Last().Id;
        return ambiguous with { Executions = ambiguous.Executions with {
            Occurrences = ambiguous.Executions.Occurrences.Select(root => root.Id != other ? root : root with { StableRouteId = "other-ability" }).ToImmutableArray() },
            Activity = ambiguous.Activity.Select(entry => entry.ExecutionStep != ExecutionStep.Cost ? entry : entry with { ExecutionId = other }).ToImmutableArray() };
    }
    private static GameSnapshot ChangedRoot(GameSnapshot state, string mutation)
    {
        var root = state.Executions!.Occurrences.Single();
        if (mutation == "changed-kind") return state with { Executions = state.Executions with { Occurrences = [root with { Kind = ExecutionKind.Spell }] } };
        var parent = new ExecutionOccurrence("other-root", "other-root", ExecutionKind.Spell, ExecutionStep.Complete) {
            ActorPlayerId = 0, Outcome = ExecutionOutcome.Resolved };
        return state with { Executions = state.Executions with { Occurrences = [parent, mutation == "changed-parent"
            ? root with { ParentId = parent.Id } : root with { BudgetRootId = parent.Id }] } };
    }
    private sealed class OfferedPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("route-alias-probe", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var source = request.Frame.SourceActions.Single();
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new UseOfferedAction(request.Frame.State.Id,
                request.Frame.Stamp.DecisionId, source.SourceCardKey, source.Actions.Single().Id), "current-offer"));
        }
    }
    private sealed class ProgressProvider : IAutomatedSeatProvider
    {
        internal AutomatedSeatObservation Observation = null!;
        internal List<GameCommand> Commands { get; } = [];
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
            => Task.FromResult(ImmutableArray.Create(Observation));
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (seatId != Observation.SeatId || command is not DecisionCommand decision
                || !new AiDecisionFrame(Observation.State, domain: Observation.Domain, semantics: Observation.Semantics).Allows(decision))
                throw new EngineCommandRejectedException("The route progress probe received an unauthorized command.");
            Commands.Add(command); return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default) => Task.CompletedTask;
    }

    private static AiSeatSession Session(int historyCapacity = 128) => new("route-retry-game", 0, new CasualCommanderPolicy(), 17,
        new(TimeSpan.FromSeconds(5), workUnits: 1024, historyCapacity: historyCapacity));

    private static async Task<AiSubmission?> Choose(AiSeatSession session, AiDecisionFrame frame)
    {
        session.Observe(frame);
        await session.EvaluateAsync();
        return session.TakeCommand();
    }

    private static AiDecisionFrame Frame(GameSnapshot state, string stableId)
    {
        var card = state.Viewer!.Zone("Hand").Cards.Single();
        string offerId = "current-offer-" + state.Revision;
        var semantics = new ActionSemantics(offerId, ActionEffectKind.Draw, [ActionEffectKind.Draw], [], ActionAffordability.Payable) {
            StableId = stableId
        };
        var domain = new DecisionActionDomain(state.Revision, ActionDomainCoverage.Complete,
            [new(state.Id, state.Decision!.Id, card.Key, card.VisualId, [new(offerId, "cast", "Untrusted display description")])], [semantics]);
        return new(state, domain: domain);
    }

    private static GameSnapshot Priority(string sourceId, long revision, int turn = 8)
    {
        var card = new Card { VisualId = sourceId, CombatId = sourceId, Key = sourceId + ":key-" + revision,
            Name = "Disclosed action source", Type = "Instant" };
        return new("route-retry-game", "playing", null, null, 0, turn, "Main", "MAIN1", 0, [
            new(0, "Acting seat", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 1, [card], null), new("Battlefield", 0, [], null)]),
            new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ], [], null, new("current-priority-" + revision, new(), new PriorityPayload(Enabled))) {
            Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = 0
        };
    }
}
