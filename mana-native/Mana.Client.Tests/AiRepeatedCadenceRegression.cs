using System.Collections.Immutable;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

// Authored provider/receipt journey, not a Magic rules simulator. The actual
// host, timed cadence, fresh policy inputs and both human client lanes run here.
internal static class AiRepeatedCadenceRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await MixedTable(check);
        await Boundaries(check);
        await AgedPublicParent(check);
    }

    private static async Task AgedPublicParent(Action<bool, string> check)
    {
        foreach (bool reparent in new[] { false, true }) {
            var clock = new Clock(); var pace = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
            var provider = new QueueProvider(3) { Transform = state => PublicParents(state, "initial") };
            await using var host = new AiTableHost(provider, _ => new Passing(), 42, pacing: pace);
            await host.StepAsync(); await provider.PassHuman(0); await provider.PassHuman(1);
            clock.Advance(TimeSpan.FromSeconds(2.4));
            provider.Transform = state => PublicParents(state, reparent ? "reparent" : "aged");
            await host.StepAsync();
            check(provider.BotCommands == (reparent ? 0 : 1) && host.LastFault == null,
                reparent ? "Contradictory known reparenting of an original captured trigger starts an ordinary readable beat"
                    : "An original trigger retains its previously disclosed immutable parent when that parent's event ages out of the public activity tail");
        }
    }

    private static GameSnapshot PublicParents(GameSnapshot state, string stage)
    {
        var basis = state.Executions!.Occurrences[0];
        var first = basis with { Id = "public-parent-a", BudgetRootId = "public-parent-a", Kind = ExecutionKind.Activation,
            Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved };
        var second = first with { Id = "public-parent-b", BudgetRootId = "public-parent-b" };
        var occurrences = state.Executions.Occurrences.Select((item, index) => item with {
            ParentId = stage == "reparent" && index == 0 ? second.Id : first.Id }).ToImmutableArray().Add(first).Add(second);
        var firstEvent = new ActivityEntry(1, 7, "MAIN1", "resolved", 0, "Previously announced parent", "source", null) {
            ExecutionId = first.Id, ExecutionStep = ExecutionStep.Complete };
        var secondEvent = firstEvent with { Id = 2, ExecutionId = second.Id };
        return state with { Executions = state.Executions with { Occurrences = occurrences },
            Activity = (stage == "aged" ? ImmutableArray<ActivityEntry>.Empty : [firstEvent]).Add(secondEvent)
                .AddRange(state.Activity.Select(item => item with { Id = item.Id + 2 })) };
    }

    private static async Task MixedTable(Action<bool, string> check)
    {
        var clock = new Clock(); var pace = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
        var provider = new QueueProvider(50); var policy = new Passing();
        await using var host = new AiTableHost(provider, _ => policy, 42, pacing: pace);
        var humans = Enumerable.Range(0, 2).Select(seat => new ClientSession(provider.Human(seat))).ToArray();
        var intents = new FiniteResolutionIntent?[2];
        await host.StepAsync();
        for (int seat = 0; seat < 2; seat++) {
            humans[seat].Accept(provider.View(seat));
            intents[seat] = FiniteResolutionIntent.TryArm(provider.View(seat), provider.View(seat).Stack.Select(item => item.Id));
            humans[seat].AdvanceFinite(intents[seat]!, (before, after) => after.Revision > before.Revision);
            await Pump(humans[seat]);
        }
        await host.StepAsync(); clock.Advance(TimeSpan.FromSeconds(2.39)); await host.StepAsync();
        check(provider.BotCommands == 0 && policy.Evaluations == 0,
            "A mixed two-human/two-AI finite queue retains the shipping2.4s initial readable beat without evaluating or caching a bot answer early");
        clock.Advance(TimeSpan.FromSeconds(.01));
        for (int tick = 0; provider.Completed < 50 && tick < 300; tick++) {
            await host.StepAsync();
            foreach (int seat in Enumerable.Range(0, 2)) {
                humans[seat].Accept(provider.View(seat));
                if (intents[seat]!.IsActive && !humans[seat].Busy && humans[seat].State?.Decision?.Payload is PriorityPayload) {
                    humans[seat].AdvanceFinite(intents[seat]!, (before, after) => after.Revision > before.Revision);
                    await Pump(humans[seat]);
                }
            }
        }
        foreach (int seat in Enumerable.Range(0, 2)) { humans[seat].Accept(provider.View(seat)); humans[seat].Close(); }
        check(provider.Completed == 50 && provider.BotCommands == 100 && provider.HumanCommands == 100
                && intents.All(intent => intent!.Progress is { CompletedCount: 50, ResolvedCount: 50 })
                && host.LastFault == null && clock.Elapsed == TimeSpan.FromSeconds(2.4),
            "Fifty certified triggers round-trip through two human finite executors and two actual bot host seats after one initial beat, without multiplying2.4s per trigger");
        check(policy.Evaluations == provider.BotCommands && provider.BotDecisionIds.Distinct().Count() == 100
                && provider.Commands.All(command => command is PassPriority),
            "Repeated cadence only changes waiting: every bot re-observes, evaluates and submits one fresh legal priority answer; no server grant or stored answer is used");
    }

    private static async Task Boundaries(Action<bool, string> check)
    {
        foreach (string change in new[] { "response", "retarget", "reorder", "source", "controller", "choice", "action", "phase", "outside", "unknown-parent", "private-parent" }) {
            var clock = new Clock(); var pace = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
            var provider = new QueueProvider(3);
            await using var host = new AiTableHost(provider, _ => new Passing(), 42, pacing: pace);
            await host.StepAsync(); await provider.PassHuman(0); await provider.PassHuman(1);
            clock.Advance(TimeSpan.FromSeconds(2.4));
            provider.Transform = state => Alter(state, change);
            await host.StepAsync();
            check(provider.BotCommands == 0 && !pace.Ready("cadence-game", 2),
                "A captured host cadence keeps an ordinary new readable beat for " + change + " instead of inheriting the prior group's elapsed time");
        }
        foreach (string control in new[] { "unchanged", "translated", "known-parent", "private-preparation" }) {
            var clock = new Clock(); var pace = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
            var provider = new QueueProvider(3);
            await using var host = new AiTableHost(provider, _ => new Passing(), 42, pacing: pace);
            await host.StepAsync(); await provider.PassHuman(0); await provider.PassHuman(1);
            clock.Advance(TimeSpan.FromSeconds(2.4)); provider.Transform = state => Alter(state, control);
            await host.StepAsync();
            check(provider.BotCommands == 1 && host.LastFault == null,
                "Typed host cadence preserves its elapsed beat for " + control + ", without using display prose or a shared root as ancestry");
        }
        foreach (bool legacy in new[] { false, true }) {
            var clock = new Clock(); var pace = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
            var provider = new QueueProvider(legacy ? 3 : 257);
            if (legacy) provider.Transform = state => state with { StackActionVersion = null, ResolutionChoiceVersion = null };
            await using var host = new AiTableHost(provider, _ => new Passing(), 42, pacing: pace);
            await host.StepAsync(); await provider.PassHuman(0); await provider.PassHuman(1);
            clock.Advance(TimeSpan.FromSeconds(2.4)); await host.StepAsync(); await host.StepAsync();
            await provider.PassHuman(0); await provider.PassHuman(1); await host.StepAsync();
            check(provider.BotCommands == 2 && !pace.Ready("cadence-game", 2),
                legacy ? "A provider without typed cadence evidence retains ordinary per-resolution timing"
                    : "A queue beyond256captured members keeps ordinary timing rather than widening a finite cadence cohort");
        }
    }

    private static GameSnapshot Alter(GameSnapshot state, string change)
    {
        if (change == "unchanged") return state;
        if (change == "translated") return state with { Stack = state.Stack.Select(item => item with { Name = "Other name", Text = "Other language" }).ToImmutableArray() };
        if (change == "retarget") return state with { Stack = state.Stack.SetItem(0, state.Stack[0] with { Targets = [new("player", "3", "Seat3", 3)] }) };
        if (change == "reorder") return state with { Stack = state.Stack.Reverse().ToImmutableArray() };
        if (change == "source") return state with { Stack = state.Stack.SetItem(0, state.Stack[0] with { Repetition = new("changed-source-terms", "source") }) };
        if (change == "controller") return state with {
            Stack = state.Stack.SetItem(0, state.Stack[0] with { ControllerId = 1 }),
            Executions = state.Executions! with { Occurrences = state.Executions.Occurrences.SetItem(0, state.Executions.Occurrences[0] with { ActorPlayerId = 1 }) } };
        if (change == "choice") return state with { ResolutionChoiceVersion = 2 };
        if (change == "action") return state with { StackActionVersion = 2 };
        if (change == "phase") return state with { PhaseKey = "MAIN2" };
        if (change == "outside") return state with { Activity = state.Activity.Add(new(99, 7, "MAIN1", "damage", 1, "Public unrelated damage", null, null)) };
        var original = state.Executions!.Occurrences[0];
        if (change == "private-preparation") return state with { Executions = state.Executions with {
            Occurrences = state.Executions.Occurrences.Add(original with { Id = "private-preparation", BudgetRootId = "private-preparation",
                Kind = ExecutionKind.Spell, Step = ExecutionStep.Preparation, Outcome = null }) } };
        if (change == "private-parent") {
            var hidden = original with { Id = "private-parent", ParentId = original.Id, Step = ExecutionStep.Resolution, Outcome = null };
            var child = original with { Id = "public-child", ParentId = hidden.Id, Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved };
            return state with { Executions = state.Executions with { Occurrences = state.Executions.Occurrences.Add(hidden).Add(child) },
                Activity = state.Activity.Add(new(99, 7, "MAIN1", "resolved", 0, "Public child", "source", null) {
                    ExecutionId = child.Id, ExecutionStep = ExecutionStep.Complete }) };
        }
        var added = original with { Id = "new-execution", BudgetRootId = original.Id,
            ParentId = change == "known-parent" ? original.Id : null, Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved };
        var next = state with { Executions = state.Executions with { Occurrences = state.Executions.Occurrences.Add(added) },
            Activity = state.Activity.Add(new(99, 7, "MAIN1", "resolved", 0, "Public effect", "source", null) {
                ExecutionId = added.Id, ExecutionStep = ExecutionStep.Complete }) };
        return change == "response" ? next with { Stack = state.Stack.Insert(0, state.Stack[0] with { Id = "fresh-response", ExecutionId = added.Id }),
            Executions = next.Executions with { Occurrences = next.Executions.Occurrences.SetItem(next.Executions.Occurrences.Length - 1,
                added with { BudgetRootId = added.Id, Step = ExecutionStep.Stack, Outcome = null }) } } : next;
    }

    private sealed class Clock : TimeProvider
    {
        internal TimeSpan Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        internal void Advance(TimeSpan value) => Elapsed += value;
    }
    private sealed class Passing : IAiPolicy
    {
        internal int Evaluations;
        public AiPolicyDescriptor Descriptor { get; } = new("cadence-only.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Evaluations++;
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new PassPriority(request.Frame.State.Id,
                request.Frame.State.Decision!.Id), "fresh-priority"));
        }
    }
    private static async Task Pump(ClientSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do { session.Pump(); await Task.Delay(1); } while (session.Busy && DateTime.UtcNow < deadline);
        session.Pump(); if (session.Busy) throw new TimeoutException("The authored human priority receipt did not reconcile.");
    }

    private sealed class QueueProvider(int count) : IAutomatedSeatProvider
    {
        private readonly object gate = new();
        private int completed, responder;
        private long revision = 1;
        internal int Completed { get { lock (gate) return completed; } }
        internal int HumanCommands, BotCommands;
        internal List<GameCommand> Commands { get; } = [];
        internal List<string> BotDecisionIds { get; } = [];
        internal Func<GameSnapshot, GameSnapshot>? Transform;
        internal GameSnapshot View(int viewer)
        {
            lock (gate) {
                var source = new Card { Key = "current-source", VisualId = "source", CombatId = "source", Name = "Public trigger source", Type = "Artifact" };
                var occurrences = Enumerable.Range(0, count).Select(index => new ExecutionOccurrence("execution-" + index, "execution-" + index,
                    ExecutionKind.Trigger, index < completed ? ExecutionStep.Complete : ExecutionStep.Stack) {
                        ActorPlayerId = 0, SourceCardId = "source", Outcome = index < completed ? ExecutionOutcome.Resolved : null }).ToImmutableArray();
                var stack = Enumerable.Range(completed, count - completed).Select(index => new StackItem("Public trigger source", "Repeated public ability",
                    StackActionKind.TriggeredAbility, source) { Id = "stack-" + index, ExecutionId = "execution-" + index,
                        ControllerId = 0, Repetition = new("same-rules-source-targets", "source"), Targets = [new("player", "0", "Human0", 0)] }).ToImmutableArray();
                var activity = ImmutableArray.Create(new ActivityEntry(1, 7, "MAIN1", "ability", 0, "Group announced", "source", null) {
                    ExecutionId = "execution-0", ExecutionStep = ExecutionStep.Stack })
                    .AddRange(Enumerable.Range(0, completed).Select(index => new ActivityEntry(index + 2, 7, "MAIN1", "resolved", 0,
                        "Public trigger resolved", "source", null) { ExecutionId = "execution-" + index, ExecutionStep = ExecutionStep.Complete }));
                bool finished = completed == count;
                var state = new GameSnapshot("cadence-game", "playing", null, null, viewer, 7, "Main", "MAIN1", 0,
                    Enumerable.Range(0, 4).Select(seat => new Player(seat, seat < 2 ? "Human" : "AI", 40, seat == viewer, false,
                        ImmutableDictionary<string, int>.Empty, seat == 0 ? [new("Battlefield", 1, [source], null)] : [])).ToImmutableArray(), stack, null,
                    !finished && viewer == responder ? new("priority-" + completed + "-" + viewer + "-" + revision, new(), new PriorityPayload(new("Pass", true))) : null) {
                        Revision = revision, BoardRevision = revision, DecisionOwnerId = finished ? null : responder, PriorityPlayerId = finished ? null : responder,
                        ActionClass = finished ? "resolving" : "priority", StackInterruptionVersion = 1 + completed,
                        StackActionVersion = 1, ResolutionChoiceVersion = 1, Activity = activity,
                        Executions = new(1, completed, occurrences, occurrences.Take(completed).Select((item, index) => new ExecutionTransition(index + 1, item.Id,
                            ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }).ToImmutableArray()) };
                return Transform?.Invoke(state) ?? state;
            }
        }
        private Task Pass(int seat, GameCommand command)
        {
            lock (gate) {
                if (seat != responder || completed == count || command is not PassPriority || !GameCommandPermissions.Allows(View(seat), command))
                    throw new EngineCommandRejectedException("The authored provider requires its exact current responder and input.");
                Commands.Add(command);
                if (seat >= 2) { BotCommands++; BotDecisionIds.Add(((PassPriority)command).DecisionId); } else HumanCommands++;
                revision++; if (++responder == 4) { responder = 0; completed++; }
                return Task.CompletedTask;
            }
        }
        internal Task PassHuman(int seat) => Pass(seat, new PassPriority(View(seat).Id, View(seat).Decision!.Id));
        internal IMatchConnection Human(int seat) => new HumanConnection(this, seat);
        public Task EnableAutomatedSeatsAsync() => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync()
            => Task.FromResult(Enumerable.Range(2, 2).Select(seat => {
                var state = View(seat);
                return new AutomatedSeatObservation(seat, state, state.Decision == null ? DecisionActionDomain.None
                    : new(state.Revision, ActionDomainCoverage.Complete, [], []), new());
            }).ToImmutableArray());
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command) => Pass(seatId, command);
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode) => throw new Exception("Unexpected authored bot fault: " + reasonCode);
        private sealed class HumanConnection(QueueProvider owner, int seat) : IMatchConnection
        {
            public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(owner.View(seat));
            public Task ExecuteAsync(GameCommand command) => owner.Pass(seat, command);
            public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("The cadence fixture never concedes.");
        }
    }
}
