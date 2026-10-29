using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class FiniteResolutionRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        ProgressAndBoundaries(check);
        await StreamsEveryOutcome(check, decline: false, count: 71);
        await StreamsEveryOutcome(check, decline: true, count: 55);
        await FreshInputsAndLineage(check);
        await Lifetime(check);
        await NoDuplicatePublication(check);
        await InterleavedQueue(check);
    }

    private static async Task InterleavedQueue(Action<bool, string> check)
    {
        var initial = InterleavedState(0, paying: true, revision: 1);
        var offer = ((PaymentPayload)initial.Decision!.Payload).OptionalDecline!;
        var intent = FiniteResolutionIntent.TryArmQueueWithDecline(initial, initial.Stack.Select(item => item.Id), offer)!;
        check(intent is { Progress.CapturedCount: 110, DeclineProgress.CapturedCount: 55 }
                && FiniteResolutionIntent.TryArm(initial, initial.Stack.Select(item => item.Id), offer) == null
                && FiniteResolutionIntent.TryArmQueueWithDecline(initial, initial.Stack.Skip(1).Select(item => item.Id), offer) == null,
            "Mixed queue consent explicitly captures all110current occurrences while only55same-source payments can be declined; subset or legacy consent cannot silently widen");
        var port = new InterleavedPort(initial);
        using var lifetime = new SessionLifetime(port, initial);
        int terminals = 0;
        lifetime.Session.Changed += (_, _) => terminals += lifetime.Session.ExecutionChanges.Transitions.Count(item => item.Step == ExecutionStep.Complete);
        for (int batch = 0; intent.IsActive && batch < 16; batch++) {
            lifetime.Session.AdvanceFinite(intent, (_, _) => false); await Pump(lifetime.Session);
        }
        check(intent.StopReason == "completed" && intent.Progress is { CompletedCount: 110, DeclinedCount: 55, ResolvedCount: 55 }
                && intent.DeclineProgress is { CapturedCount: 55, CompletedCount: 55, DeclinedCount: 55, ResolvedCount: 0 }
                && terminals == 110 && port.Commands.OfType<CancelPayment>().Count() == 55
                && port.Commands.OfType<PassPriority>().Count() == 109 && !lifetime.Session.NeedsRecovery,
            "An alternating55optional-cost/55ordinary-trigger queue declines only the certified group, yields fresh priority for the rest and retains every actual terminal outcome");

        foreach (var changed in new[] {
            InterleavedState(2, paying: true, revision: 2) with { ResolutionChoiceVersion = 2 },
            InterleavedState(2, paying: true, revision: 2) with { Decision = Payment(2, previous: "unobserved-other-offer") },
            InterleavedState(2, paying: true, revision: 2) with { Decision = Payment(2, previous: "offer-0", paymentKey: "changed-cost") },
            InterleavedState(2, paying: true, revision: 2) with { Decision = Payment(2, previous: "offer-0", groupKey: "other-trigger-group") }
        }) {
            var bounded = FiniteResolutionIntent.TryArmQueueWithDecline(initial, initial.Stack.Select(item => item.Id), offer)!;
            check(!bounded.CanContinue(changed),
                "Whole-queue priority consent never authorizes a different optional payment, changed cost, missing offer lineage or intervening own required choice");
        }
        var afterFirst = InterleavedState(1, paying: false, revision: 2) with {
            FiniteStackYield = new("", FiniteStackYieldStatus.Idle, [], [], 1, 1) };
        var server = FiniteResolutionIntent.TryArmQueueWithDecline(initial, initial.Stack.Select(item => item.Id), offer)!;
        var sequence = new Sequence(afterFirst);
        using var supported = new SessionLifetime(sequence, initial);
        supported.Session.AdvanceFinite(server, (before, after) => after.Revision > before.Revision); await Pump(supported.Session);
        supported.Session.AdvanceFinite(server, (_, _) => false);
        await Pump(supported.Session);
        check(sequence.Commands.OfType<GrantFiniteStackYield>().SingleOrDefault() is { ExecutionIds.Length: 109 } grant
                && grant.ExecutionIds.ToHashSet().SetEquals(afterFirst.Stack.Select(item => item.ExecutionId))
                && !grant.ExecutionIds.Contains(offer.ProducingTriggerExecutionId),
            "After the first direct payment, a supported mixed batch grants exactly the currently queued109executions, never the off-stack or completed member");
    }

    private static GameSnapshot InterleavedState(int completed, bool paying, long revision)
    {
        var outcomes = Enumerable.Range(0, 110).Select(index => index % 2 == 0 ? ExecutionOutcome.Declined : ExecutionOutcome.Resolved).ToImmutableArray();
        var state = State(110, completed, paying, revision, outcomes);
        var other = state.Viewer!.Zone("Battlefield").Cards[0] with { Key = "other-current", VisualId = "other-source", CombatId = "other-source", Name = "Other trigger source" };
        return state with {
            Players = state.Players.SetItem(0, state.Viewer with { Zones = [new("Battlefield", 2, [state.Viewer.Zone("Battlefield").Cards[0], other], null)] }),
            Executions = state.Executions! with { Occurrences = state.Executions.Occurrences.Select((item, index) => index % 2 == 0 ? item
                : item with { SourceCardId = "other-source", SourceName = other.Name }).ToImmutableArray() },
            Stack = state.Stack.Select(item => int.Parse(item.ExecutionId.AsSpan("execution-".Length)) % 2 == 0 ? item
                : item with { Card = other, Repetition = new("other-trigger-group", "other-source") }).ToImmutableArray(),
            Decision = paying ? Payment(completed, previous: completed == 0 ? "" : "offer-" + (completed - 2)) : state.Decision
        };
    }

    private sealed class InterleavedPort(GameSnapshot state) : IMatchConnection
    {
        internal List<GameCommand> Commands { get; } = [];
        public Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(state, command)) throw new EngineCommandRejectedException("Stale mixed queue input");
            int completed = state.Executions!.Occurrences.Count(item => item.Step == ExecutionStep.Complete);
            if (command is CancelPayment && completed % 2 == 0 && state.Decision?.Payload is PaymentPayload)
                completed++;
            else if (command is PassPriority && state.Decision?.Payload is PriorityPayload) {
                if (completed % 2 == 1) completed++;
                else { Commands.Add(command); state = InterleavedState(completed, paying: true, state.Revision + 1); return Task.CompletedTask; }
            } else throw new InvalidOperationException("A mixed batch attempted an unconsented choice");
            Commands.Add(command); state = InterleavedState(completed, paying: false, state.Revision + 1);
            return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(state);
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }

    private static void ProgressAndBoundaries(Action<bool, string> check)
    {
        var initial = State(3);
        var intent = Arm(initial)!;
        var resolving = initial with { Revision = 2, Decision = null, DecisionOwnerId = null, PriorityPlayerId = null,
            ActionClass = "resolving", Stack = initial.Stack.RemoveAt(0), Executions = initial.Executions! with {
                Occurrences = initial.Executions.Occurrences.SetItem(0, initial.Executions.Occurrences[0] with { Step = ExecutionStep.Resolution }) } };
        check(intent.CanContinue(resolving) && intent.Progress is { CompletedCount: 0, RemainingCount: 3 },
            "A captured occurrence leaving the stack is still pending until its actual terminal execution outcome is observed");
        var first = State(3, completed: 1, revision: 3);
        check(intent.CanContinue(first) && intent.Progress is { CapturedCount: 3, CompletedCount: 1, ResolvedCount: 1, RemainingCount: 2 }
                && intent.CanContinue(initial) && intent.Progress.CompletedCount == 1,
            "Terminal execution facts advance finite progress once while an older UI frame cannot erase or enlarge it");
        var final = State(3, completed: 3, revision: 4, outcomes: [ExecutionOutcome.Resolved, ExecutionOutcome.Declined, ExecutionOutcome.Countered]);
        check(!intent.CanContinue(final) && !intent.IsActive && intent.StopReason == "completed"
                && intent.Progress is { CompletedCount: 3, RemainingCount: 0, ResolvedCount: 1, DeclinedCount: 1, CounteredCount: 1 },
            "Finite progress preserves distinct resolved, declined and countered terminal outcomes instead of assuming every departed item resolved");
        var lost = resolving with { Executions = resolving.Executions! with { Occurrences = resolving.Executions.Occurrences.RemoveAt(0) } };
        intent = Arm(initial)!;
        check(!intent.CanContinue(lost) && intent.Progress is { CompletedCount: 0, UnknownCount: 1, RemainingCount: 3 },
            "Missing terminal evidence stops the finite intent with an explicit unknown outcome rather than manufacturing completion");

        foreach (var changed in new[] {
            initial with { Id = "other-game" }, initial with { ViewerId = 1 }, initial with { Turn = 8 },
            initial with { PhaseKey = "MAIN2" }, initial with { ActivePlayerId = 0 },
            initial with { StackActionVersion = 2 }, initial with { ResolutionChoiceVersion = 2 },
            initial with { StackActionVersion = null }, initial with { ResolutionChoiceVersion = null },
            initial with { Stack = initial.Stack.Reverse().ToImmutableArray() },
            initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with { ControllerId = 1 }) },
            initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with { Targets = [new("player", "1", "Opponent", 1)] }) },
            initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with { Repetition = new("different-purpose", "source") }) },
            initial with { Executions = initial.Executions! with { Occurrences = initial.Executions.Occurrences.SetItem(0,
                initial.Executions.Occurrences[0] with { BudgetRootId = "execution-1" }) } },
            initial with { Decision = Payment(0), PriorityPlayerId = null, ActionClass = "payment",
                Executions = initial.Executions! with { Occurrences = initial.Executions.Occurrences.SetItem(0,
                    initial.Executions.Occurrences[0] with { Step = ExecutionStep.Cost }) } },
            initial with { Decision = new("required-number", new(), new NumberPayload("1", new(0, 3))) }
        }) {
            intent = Arm(initial)!;
            check(!intent.CanContinue(changed with { Revision = 2 }) && !intent.IsActive,
                "Finite consent retires on changed scope, action/choice fence, occurrence ownership, queue order, targets, terms or an unconsented input");
        }
        var outsider = initial.Stack[0] with { Id = "future-same-named-trigger", ExecutionId = "future-execution" };
        var future = initial with { Revision = 2, Stack = initial.Stack.Insert(0, outsider), Executions = initial.Executions! with {
            Occurrences = initial.Executions.Occurrences.Add(new("future-execution", "future-execution", ExecutionKind.Trigger, ExecutionStep.Stack) {
                ActorPlayerId = 0, SourceCardId = "source" }) } };
        check(!Arm(initial)!.CanContinue(future), "An identical-looking future trigger does not join a previously captured finite queue");
        var parent = initial.Executions!.Occurrences[0];
        var child = new ExecutionOccurrence("explicit-child", parent.BudgetRootId, ExecutionKind.Rules, ExecutionStep.Resolution) {
            ParentId = parent.Id, RuleKind = ExecutionRuleKind.Replacement };
        var unrelated = child with { Id = "same-root-only", ParentId = null };
        var family = initial with { Executions = initial.Executions with { Occurrences = initial.Executions.Occurrences.Add(child).Add(unrelated) } };
        intent = Arm(initial)!;
        check(intent.IncludesExecution(family, child.Id) && !intent.IncludesExecution(family, unrelated.Id),
            "Presentation batch membership follows explicit parent ancestry and never a shared budget root alone");

        var optional = State(3, paying: true);
        var offer = ((PaymentPayload)optional.Decision!.Payload).OptionalDecline!;
        check(Arm(optional, offer) is { DeclinesOptionalPayments: true } decline && decline.Progress.CapturedCount == 3
                && Arm(optional) == null && Arm(optional with { StackActionVersion = null }, offer) == null,
            "Optional negative consent explicitly captures the currently resolving trigger plus its matching queued occurrences, while legacy or ordinary pass consent cannot answer it");
        var translated = optional with { Decision = optional.Decision with { Context = new("Completely different text"),
            Payload = (PaymentPayload)optional.Decision.Payload with { Pay = new("Translated affirmative", true), Cancel = new("Translated negative", true) } },
            Stack = optional.Stack.Select(item => item with { Name = "Different name", Text = "Unrelated translated description" }).ToImmutableArray() };
        check(Arm(translated, offer) != null, "Finite optional matching uses provider provenance and terms rather than card names, labels or rendered prose");
        foreach (var unsupported in new[] {
            optional with { Decision = optional.Decision with { Payload = ((PaymentPayload)optional.Decision.Payload) with { OptionalDecline = null } } },
            optional with { Decision = optional.Decision with { Payload = ((PaymentPayload)optional.Decision.Payload) with { Cancel = new("No", false) } } },
            optional with { Decision = optional.Decision with { Payload = ((PaymentPayload)optional.Decision.Payload) with { OptionalDecline = offer with { Mandatory = true } } } },
            optional with { Decision = optional.Decision with { Payload = ((PaymentPayload)optional.Decision.Payload) with { OptionalDecline = offer with { PayerId = 1 } } } },
            optional with { Decision = optional.Decision with { Payload = ((PaymentPayload)optional.Decision.Payload) with { OptionalDecline = offer with { Purpose = PaymentPurpose.SpellCost } } } },
            optional with { Decision = optional.Decision with { Payload = ((PaymentPayload)optional.Decision.Payload) with { OptionalDecline = offer with { ProducingTriggerExecutionId = "execution-1" } } } }
        }) check(Arm(unsupported, offer) == null,
            "An absent, mandatory, uncancellable, foreign-payer, spell-cost or unlinked payment cannot arm optional negative repetition");
        var otherSeatChoice = initial with { Revision = 2, Decision = null, DecisionOwnerId = 1, PriorityPlayerId = null,
            ActionClass = "choice", StackInterruptionVersion = 11 };
        check(Arm(initial)!.CanContinue(otherSeatChoice),
            "A different seat's owned choice can be observed without answering it or revoking this seat's unchanged finite consent");
    }

    private static async Task StreamsEveryOutcome(Action<bool, string> check, bool decline, int count)
    {
        var connection = new Resolving(State(count, paying: decline), decline);
        using var lifetime = new SessionLifetime(connection);
        var session = lifetime.Session;
        var intent = Arm(connection.State, decline ? ((PaymentPayload)connection.State.Decision!.Payload).OptionalDecline : null)!;
        var revisions = new List<long>(); var terminals = new List<string>();
        session.Changed += (_, state) => {
            if (state != null) revisions.Add(state.Revision);
            terminals.AddRange(session.ExecutionChanges.Transitions.Where(item => item.Step == ExecutionStep.Complete).Select(item => item.ExecutionId));
        };
        int batches = 0; bool bounded = true;
        while (intent.IsActive && batches++ < 16) {
            int before = connection.Commands.Count;
            check(session.AdvanceFinite(intent, (_, _) => false), "A live finite batch starts only through the session's serialized command lane");
            await Pump(session);
            bounded &= connection.Commands.Count - before is > 0 and <= 32;
        }
        int expected = decline ? count * 2 - 1 : count;
        check(!intent.IsActive && intent.StopReason == "completed" && intent.Progress.CompletedCount == count && bounded
                && connection.Commands.Count == expected && connection.Observations == expected && revisions.Count == expected
                && revisions.Distinct().Count() == expected && terminals.Distinct().Count() == count && terminals.Count == count,
            "Finite batches preserve every intermediate immutable observation and exact terminal receipt, publish no duplicate final frame, and yield within 32 ordinary commands: " + (decline ? "55 optional declines" : "71 priority passes"));
        check(connection.Commands.All(command => command is PassPriority or CancelPayment)
                && (!decline || intent.Progress.DeclinedCount == count)
                && connection.Commands.OfType<PassPriority>().All(command => command.StackInterruptionVersion != null),
            "Repeated optional consent only declines the certified captured costs and passes fresh fenced priority; it never auto-pays, accepts or broadens a decision");
    }

    private static async Task FreshInputsAndLineage(Action<bool, string> check)
    {
        var initial = State(3, paying: true);
        var offer = ((PaymentPayload)initial.Decision!.Payload).OptionalDecline!;
        foreach (var changed in new[] {
            State(3, completed: 1, paying: true, revision: 2) with { Decision = Payment(1, previous: "unseen-other-purpose") },
            State(3, completed: 1, paying: true, revision: 2) with { Decision = Payment(1, paymentKey: "changed-cost") },
            State(3, completed: 1, paying: true, revision: 2) with { Decision = Payment(1, groupKey: "different-ability") }
        }) {
            var intent = Arm(initial, offer)!;
            check(!intent.CanContinue(changed) && !intent.IsActive,
                "A skipped optional offer, changed cost or different ability cannot refresh finite repeated-decline consent");
        }
        var sameEpisode = initial with { Revision = 2, Decision = initial.Decision! with { Id = "refreshed-same-input" } };
        var next = State(3, completed: 1, revision: 3);
        var connection = new Sequence(sameEpisode, next);
        using (var lifetime = new SessionLifetime(connection, initial)) {
            var intent = Arm(initial, offer)!;
            check(lifetime.Session.AdvanceFinite(intent, (_, after) => after.Revision >= 3), "The same actual optional input can refresh its prompt UUID without creating new consent");
            await Pump(lifetime.Session);
            check(connection.Commands.Count == 1 && connection.Commands[0] is CancelPayment,
                "An accepted optional decline is never submitted twice through a refreshed prompt UUID for the same actual OfferId");
        }
        var priority = State(2);
        connection = new Sequence(priority with { Revision = 2, Decision = priority.Decision! with { Id = "fresh-without-progress" } });
        using (var lifetime = new SessionLifetime(connection, priority)) {
            var intent = Arm(priority)!; lifetime.Session.AdvanceFinite(intent, (_, _) => false); await Pump(lifetime.Session);
            check(!intent.IsActive && connection.Commands.Count == 1,
                "Fresh legal priority handles without queue or terminal progress cannot produce an endless finite batch");
        }

        var subset = State(3);
        var onlyFirst = FiniteResolutionIntent.TryArm(subset, ["queued-0"])!;
        var subsetConnection = new Resolving(subset, decline: false);
        using (var lifetime = new SessionLifetime(subsetConnection)) {
            lifetime.Session.AdvanceFinite(onlyFirst, (_, _) => false); await Pump(lifetime.Session);
            check(subsetConnection.Commands.Count == 1 && onlyFirst.Progress is { CapturedCount: 1, CompletedCount: 1 }
                    && lifetime.Session.State!.Stack.Length == 2,
                "Explicit subset consent resolves only its captured occurrence and leaves other original stack items for a fresh decision");
        }
    }

    private static async Task Lifetime(Action<bool, string> check)
    {
        var initial = State(2);
        var after = State(2, completed: 1, revision: 2);
        foreach (bool stop in new[] { false, true }) {
            var connection = new Held(after);
            using var lifetime = new SessionLifetime(connection, initial);
            var intent = Arm(initial)!;
            check(lifetime.Session.AdvanceFinite(intent, (_, _) => false), "A controlled finite command starts before interruption");
            await connection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop) intent.Stop(); else intent.Pause();
            lifetime.Session.CancelAdvance(); connection.Release.TrySetResult(); await Pump(lifetime.Session);
            check(connection.Commands.Count == 1 && connection.Observations == 1 && lifetime.Session.State == after
                    && intent.IsActive == !stop && (stop || intent.IsPaused && intent.Progress.CompletedCount == 1),
                "Pause or Take control always reconciles the in-flight command before restoring control; only a pause retains the exact remaining consent");
            if (!stop) {
                check(!lifetime.Session.AdvanceFinite(intent, (_, _) => false), "A paused finite policy cannot dispatch another command");
                intent.Resume();
                check(intent.CanContinue(after), "Resume preserves the original finite scope and fences rather than rearming a new queue");
            }
        }
        foreach (bool rejected in new[] { false, true }) {
            var connection = new Failed(rejected);
            using var lifetime = new SessionLifetime(connection, initial);
            var intent = Arm(initial)!; lifetime.Session.AdvanceFinite(intent, (_, _) => false); await Pump(lifetime.Session);
            check(!intent.IsActive && connection.Count == 1 && lifetime.Session.NeedsRecovery == !rejected
                    && lifetime.Session.UncertainCommand == !rejected && !lifetime.Session.AdvanceFinite(intent, (_, _) => false),
                "A rejected or uncertain finite command stops without retrying or treating a missing acknowledgement as a completed outcome");
        }
        var resetConnection = new Held(after);
        using (var lifetime = new SessionLifetime(resetConnection, initial)) {
            var intent = Arm(initial)!; lifetime.Session.AdvanceFinite(intent, (_, _) => false);
            await resetConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Session.Reset(); resetConnection.Release.TrySetResult(); await Pump(lifetime.Session);
            check(!intent.IsActive && lifetime.Session.State == null && resetConnection.Observations == 1,
                "Reset revokes finite consent and discards its late publication while still observing the already sent command");
        }
    }

    private static async Task NoDuplicatePublication(Action<bool, string> check)
    {
        var initial = State(2);
        var intent = Arm(initial)!;
        var connection = new EchoPause(initial, intent);
        using var lifetime = new SessionLifetime(connection, initial);
        int publications = 0;
        lifetime.Session.Changed += (_, _) => publications++;
        lifetime.Session.AdvanceFinite(intent, (_, _) => false); await Pump(lifetime.Session);
        check(connection.Commands == 1 && connection.Observations == 1 && publications == 0
                && intent.IsActive && intent.IsPaused && intent.Progress.CompletedCount == 0,
            "Pausing after an acknowledged command with an unchanged observation does not republish the already accepted baseline or invent an outcome");
        var malformed = initial with { Revision = 2,
            Stack = initial.Stack.SetItem(0, initial.Stack[0] with { Repetition = new("", "source") }) };
        bool rejected = false;
        try { lifetime.Session.Accept(malformed); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && lifetime.Session.State == initial && publications == 0,
            "Direct observation delivery validates typed finite facts before committing a replica or notifying any presentation consumer");
    }

    internal static FiniteResolutionIntent? Arm(GameSnapshot state, OptionalDeclineOffer? offer = null)
        => FiniteResolutionIntent.TryArm(state, state.Stack.Select(item => item.Id), offer);

    private static Decision Payment(int index, string? previous = null, string paymentKey = "cost-structure", string groupKey = "trigger-structure")
        => new("payment-" + index, new(), new PaymentPayload(new("Pay", true), new("Cancel", true)) {
            PayerId = 0, OptionalDecline = new(groupKey, paymentKey, "execution-" + index, "source", PaymentPurpose.EffectCost,
                0, false, "offer-" + index, previous ?? (index == 0 ? "" : "offer-" + (index - 1)))
        }) { ExecutionId = "execution-" + index, ExecutionStep = ExecutionStep.Cost };

    internal static GameSnapshot State(int count, int completed = 0, bool paying = false, long revision = 1,
        ImmutableArray<ExecutionOutcome> outcomes = default)
    {
        var source = new Card { Key = "source-current", VisualId = "source", CombatId = "source", Name = "Disclosed source", Type = "Artifact" };
        var occurrences = Enumerable.Range(0, count).Select(index => new ExecutionOccurrence("execution-" + index, "execution-" + index,
            ExecutionKind.Trigger, index < completed ? ExecutionStep.Complete : paying && index == completed ? ExecutionStep.Cost : ExecutionStep.Stack) {
                ActorPlayerId = 0, SourceCardId = "source", SourceName = "Disclosed source",
                Outcome = index < completed ? outcomes.IsDefault ? ExecutionOutcome.Resolved : outcomes[index] : null
            }).ToImmutableArray();
        var transitions = occurrences.Take(completed).Select((item, index) => new ExecutionTransition(index + 1, item.Id, ExecutionStep.Complete) {
            Outcome = item.Outcome }).ToImmutableArray();
        bool finished = completed == count;
        var stack = Enumerable.Range(completed + (paying ? 1 : 0), count - completed - (paying ? 1 : 0))
            .Select(index => new StackItem("Disclosed source", "Public ability text", StackActionKind.TriggeredAbility, source) {
                Id = "queued-" + index, ExecutionId = "execution-" + index, ControllerId = 0,
                Repetition = new("trigger-structure", "source"), Targets = [new("player", "0", "You", 0)] }).ToImmutableArray();
        return new("finite-game", "playing", null, null, 0, 7, "Main", "MAIN1", 1,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [source], null)]),
             new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], stack, null,
            finished ? null : paying ? Payment(completed) : new("priority-" + completed, new(), new PriorityPayload(new("Pass", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = finished ? null : 0, PriorityPlayerId = finished || paying ? null : 0,
                ActionClass = finished ? "resolving" : paying ? "payment" : "priority", StackInterruptionVersion = 10 + completed + (paying ? 1 : 0),
                StackActionVersion = 1, ResolutionChoiceVersion = 1, Executions = new(1, completed, occurrences, transitions)
            };
    }

    private static async Task Pump(ClientSession session)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (session.Busy && DateTime.UtcNow < end) { session.Pump(); await Task.Delay(1); }
        session.Pump(); if (session.Busy) throw new TimeoutException("Finite batch did not release its serial lane.");
    }
    private sealed class SessionLifetime : IDisposable
    {
        internal ClientSession Session { get; }
        internal SessionLifetime(IMatchConnection connection, GameSnapshot? initial = null)
        { Session = new(connection); Session.Accept(initial ?? ((Resolving)connection).State); }
        public void Dispose() => Session.Close();
    }
    private sealed class Resolving(GameSnapshot state, bool decline) : IMatchConnection
    {
        internal GameSnapshot State { get; private set; } = state;
        internal List<GameCommand> Commands { get; } = [];
        internal int Observations;
        public Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(State, command)) throw new InvalidOperationException("Stale authored finite command.");
            command.Validate(); Commands.Add(command);
            int count = State.Executions!.Occurrences.Length;
            int done = State.Executions.Occurrences.Count(item => item.Step == ExecutionStep.Complete);
            bool paying = command is PassPriority && decline;
            int completed = done + (command is CancelPayment || !decline ? 1 : 0);
            State = FiniteResolutionRegression.State(count, completed, paying, State.Revision + 1,
                decline ? Enumerable.Repeat(ExecutionOutcome.Declined, count).ToImmutableArray() : default);
            return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(State); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class Sequence(params GameSnapshot[] states) : IMatchConnection
    {
        internal List<GameCommand> Commands { get; } = [];
        private int index;
        public Task ExecuteAsync(GameCommand command) { command.Validate(); Commands.Add(command); return Task.CompletedTask; }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(states[Math.Min(index++, states.Length - 1)]);
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class Held(GameSnapshot after) : IMatchConnection
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<GameCommand> Commands { get; } = [];
        internal int Observations;
        public async Task ExecuteAsync(GameCommand command) { command.Validate(); Commands.Add(command); Started.TrySetResult(); await Release.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(after); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class Failed(bool rejected) : IMatchConnection
    {
        internal int Count;
        public Task ExecuteAsync(GameCommand command) { Count++; return rejected
            ? Task.FromException(new EngineCommandRejectedException("Changed scope")) : Task.FromException(new IOException("Lost acknowledgement")); }
        public Task<GameSnapshot?> ObserveAsync() => throw new InvalidOperationException("Uncertain execution cannot be silently retried.");
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class EchoPause(GameSnapshot state, FiniteResolutionIntent intent) : IMatchConnection
    {
        internal int Commands, Observations;
        public Task ExecuteAsync(GameCommand command) { command.Validate(); Commands++; return Task.CompletedTask; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; intent.Pause(); return Task.FromResult<GameSnapshot?>(state with { }); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
