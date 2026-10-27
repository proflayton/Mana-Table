using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;

internal static class StackResolutionRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var initial = State(3);
        check(StackResolutionIntent.TryArm(initial) is { IsActive: true, Remaining: 3 }
            && !((PriorityPayload)initial.Decision!.Payload).CanAutoPass,
            "Explicit current-stack passing can be armed while playable responses keep ordinary Auto disabled");
        foreach (var unsupported in new[] { initial with { StackInterruptionVersion = null }, initial with { StackInterruptionVersion = -1 },
            initial with { Stack = [] }, initial with { Stack = [initial.Stack[0], initial.Stack[0]] },
            initial with { Decision = new("required", new(), new ConfirmationPayload(new("Choose", true))) },
            initial with { PriorityPlayerId = 1 }, initial with { Status = "resolving" } })
            check(StackResolutionIntent.TryArm(unsupported) == null,
                "An explicit stack intent requires current owned priority, a unique finite queue and a provider interruption fence");

        var intent = StackResolutionIntent.TryArm(initial)!;
        var subsequence = initial with { Revision = 2, Stack = [initial.Stack[0], initial.Stack[2]] };
        check(intent.CanContinue(subsequence) && intent.Remaining == 2 && intent.CanContinue(initial) && intent.Remaining == 2,
            "An original queued effect may remove another captured item while an older UI frame cannot expand the remaining queue");
        check(!intent.CanContinue(initial with { Revision = 3 }) && !intent.IsActive,
            "An original stack occurrence cannot reappear after its removal was observed");
        var source = new Card { VisualId = "source", Name = "Public source", Power = 1, Toughness = 1 };
        var sourceState = initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with { Card = source }) };
        intent = StackResolutionIntent.TryArm(sourceState)!;
        check(intent.CanContinue(sourceState with { Revision = 2, Stack = sourceState.Stack.SetItem(0, sourceState.Stack[0] with {
            Card = source with { Power = 4, Toughness = 4, Counters = ImmutableDictionary<string, int>.Empty.Add("P1P1", 3) } }) }),
            "Changing a queued ability's public source statistics does not mistake that same ability for a new response");
        foreach (var changed in new[] {
            initial with { Id = "other-game" }, initial with { ViewerId = 1 }, initial with { Turn = initial.Turn + 1 },
            initial with { ActivePlayerId = 0 }, initial with { PhaseKey = "MAIN2" }, initial with { Status = "finished" },
            initial with { Status = "error" }, initial with { StackInterruptionVersion = 10 }, initial with { StackInterruptionVersion = null },
            initial with { Players = initial.Players.SetItem(0, initial.Players[0] with { Eliminated = true }) },
            initial with { Stack = initial.Stack.Add(new("Outside response", "", StackActionKind.Spell, null) { Id = "outside" }) },
            initial with { Stack = initial.Stack.Reverse().ToImmutableArray() },
            initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with { ControllerId = 1 }) },
            initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with { Targets = [new("player", "1", "Other", 1)] }) },
            initial with { Decision = new("payment", new(), new PaymentPayload(new("Pay", true), new("Cancel", true))) },
            initial with { Decision = new("optional", new(), new ConfirmationPayload(new("Yes", true), new("No", true))) },
            initial with { Decision = new("target", new(), new TargetSelectionPayload(new("Choose", false), new("Cancel", false), new(1, 1), [])) },
            initial with { Decision = null, ActionClass = "choice", DecisionOwnerId = 1, PriorityPlayerId = null }
        }) {
            intent = StackResolutionIntent.TryArm(initial)!;
            check(!intent.CanContinue(changed with { Revision = 2 }) && !intent.IsActive,
                "A context change, outside intervention, queue mutation or required choice retires current-stack passing before another answer");
        }
        var waiting = initial with { Revision = 2, Decision = null, PriorityPlayerId = 1, DecisionOwnerId = 1, ActionClass = "priority" };
        intent = StackResolutionIntent.TryArm(initial)!;
        check(intent.CanContinue(waiting) && intent.CanContinue(waiting with { Revision = 3, Status = "resolving",
                DecisionOwnerId = null, PriorityPlayerId = null, ActionClass = "resolving" }),
            "Another seat's ordinary priority and a no-owner resolving handoff retain the finite intent without granting a local command");
        check(!intent.CanContinue(initial with { Revision = 4, Stack = [] }) && intent.Remaining == 0
            && !intent.CanContinue(initial with { Revision = 5 }),
            "An empty captured queue finishes the intent permanently rather than arming a later stack");
        foreach (var payload in new DecisionPayload[] {
            new EntitySelectionPayload(new("Done", true), new("Cancel", true), []),
            new OptionsPayload(new([new(7, "Option", null)], new(1, 1))),
            new OrderingPayload(new([new(7, "Order", null)], new(1, 1))), new NumberPayload("1", new(0, 2)), new TextPayload(""),
            new AllocationPayload(1, AllocationKind.Generic, "", false, [new(0, "recipient", "", null, "", 0, 1, 0, 0, 1)]),
            new AttackPayload(new("Done", true)), new BlockPayload(new("Done", true)), new RevealPayload([]),
            new OpeningHandPayload(new("Keep", true), new("Mulligan", true))
        }) {
            intent = StackResolutionIntent.TryArm(initial)!;
            check(!intent.CanContinue(initial with { Revision = 2, Decision = new("required-input", new(), payload) }),
                "An enabled required input never becomes a stack-pass answer: " + payload.GetType().Name);
        }

        await EveryEffectIsPublished(check);
        await BoundedTransportAndDuplicateFrames(check);
        await SessionLifetime(check);
    }

    private static async Task EveryEffectIsPublished(Action<bool, string> check)
    {
        var connection = new ResolvingConnection(State(71));
        var intent = StackResolutionIntent.TryArm(connection.State)!;
        var advance = new DecisionAdvance(connection);
        var published = new List<int>();
        while (intent.IsActive) {
            int before = connection.Replies.Count;
            var next = await advance.RunStackAsync(connection.State, intent, PresentationPacing.ShouldPresent);
            if (next == null || connection.Replies.Count != before + 1) throw new InvalidOperationException("A real resolution was hidden or repeated.");
            published.Add(next.Stack.Length);
        }
        check(published.SequenceEqual(Enumerable.Range(0, 71).Reverse()) && connection.Replies.Count == 71
            && connection.Observations == 71 && connection.Replies.All(command => command is PassPriority { StackInterruptionVersion: 9 })
            && connection.Replies.Cast<PassPriority>().Select(command => command.DecisionId).Distinct().Count() == 71,
            "One explicit 71-item stack intent publishes each individual effect boundary and submits 71 separately scoped fenced priority passes");

        var initial = State(2);
        var firstResolved = initial with { Revision = 2, Decision = Priority("drawn-response"), Stack = [initial.Stack[1]],
            Players = initial.Players.SetItem(0, initial.Players[0] with { Zones = [new("Hand", 1,
                [new Card { VisualId = "new-response", Name = "Newly drawn response", Type = "Instant" }], null)] }) };
        var sequence = new SequenceConnection(firstResolved, firstResolved with { Revision = 3, Stack = [] });
        intent = StackResolutionIntent.TryArm(initial)!;
        var result = await new DecisionAdvance(sequence).RunStackAsync(initial, intent, PresentationPacing.ShouldPresent);
        check(result == firstResolved && sequence.Replies.Count == 1 && intent is { IsActive: true, Remaining: 1 },
            "A newly drawn playable response is presented for its full beat without cancelling the explicitly chosen original queue");
        var externalAction = initial with { Revision = 2, StackInterruptionVersion = 10, Activity = [] };
        sequence = new SequenceConnection(externalAction);
        intent = StackResolutionIntent.TryArm(initial)!;
        result = await new DecisionAdvance(sequence).RunStackAsync(initial, intent, (_, _) => false);
        check(result == externalAction && !intent.IsActive && sequence.Replies.Count == 1,
            "An outside action that leaves the same queue and no retained activity still interrupts through the provider fence");
    }

    private static async Task BoundedTransportAndDuplicateFrames(Action<bool, string> check)
    {
        var initial = State(2);
        var waiting = initial with { Revision = 2, Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1, ActionClass = "priority" };
        var reduced = initial with { Revision = 3, Stack = [initial.Stack[1]], Decision = Priority("next-priority") };
        var connection = new SequenceConnection(initial, initial, waiting, reduced, reduced with { Revision = 4, Stack = [] });
        var intent = StackResolutionIntent.TryArm(initial)!;
        var advance = new DecisionAdvance(connection);
        var result = await advance.RunStackAsync(initial, intent, (_, _) => false);
        check(result?.Stack.IsEmpty == true && connection.Observations == 5
            && connection.Replies.Cast<PassPriority>().Select(command => command.DecisionId).SequenceEqual(["priority-2", "next-priority"]),
            "Repeated observations and another seat's priority never resubmit the already accepted local decision");
        connection = new SequenceConnection(initial with { Revision = 2, Decision = Priority("fresh-handle-without-progress") });
        intent = StackResolutionIntent.TryArm(initial)!;
        await new DecisionAdvance(connection).RunStackAsync(initial, intent, (_, _) => false);
        check(!intent.IsActive && connection.Replies.Count == 1,
            "Fresh legal priority handles without queue progress cannot create an unbounded repeated-pass loop");

        var immediate = new ResolvingConnection(State(71));
        intent = StackResolutionIntent.TryArm(immediate.State)!;
        result = await new DecisionAdvance(immediate).RunStackAsync(immediate.State, intent, (_, _) => false);
        check(immediate.Replies.Count is > 0 and <= 32 && immediate.Observations == immediate.Replies.Count
            && result!.Stack.Length >= 39 && intent.IsActive,
            "Even a caller with immediate presentation checkpoints yields within 32 passes and reconciles every submitted command");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        connection = new SequenceConnection(initial);
        intent = StackResolutionIntent.TryArm(initial)!;
        await new DecisionAdvance(connection).RunStackAsync(initial, intent, PresentationPacing.ShouldPresent, cancelled.Token);
        check(connection.Replies.Count == 0 && connection.Observations == 0 && intent.IsActive,
            "Pausing a batch before dispatch sends nothing and retains the finite intent for deliberate continuation");
    }

    private static async Task SessionLifetime(Action<bool, string> check)
    {
        var initial = State(2);
        var reduced = initial with { Revision = 2, Stack = [initial.Stack[1]], Decision = Priority("after-first") };
        foreach (bool stop in new[] { false, true }) {
            var held = new HeldConnection(reduced);
            var session = new ClientSession(held); session.Accept(initial);
            var intent = StackResolutionIntent.TryArm(initial)!;
            bool started = session.AdvanceStack(intent, PresentationPacing.ShouldPresent);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(started && session.IsAdvancing && !session.AdvanceStack(intent, PresentationPacing.ShouldPresent)
                && !session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
                "Current-stack batches exclusively own the normal serialized command lane while one priority pass is in flight");
            if (stop) intent.Stop();
            session.CancelAdvance(); held.Release.TrySetResult();
            await Pump(session);
            check(session.State == reduced && held.Replies.Count == 1 && held.Observations == 1 && !session.IsAdvancing
                && intent.IsActive == !stop,
                stop ? "Take control reconciles the one in-flight pass before restoring manual control and never rearms the intent"
                    : "Hover pause reconciles the one in-flight pass while preserving the exact remaining intent for a later readable beat");
            if (stop) check(!session.AdvanceStack(intent, PresentationPacing.ShouldPresent) && held.Replies.Count == 1,
                "A stopped intent cannot submit a later response through the session");
            else {
                held.After = reduced with { Revision = 3, Stack = [] };
                bool resumed = session.AdvanceStack(intent, PresentationPacing.ShouldPresent); await Pump(session);
                check(resumed && held.Replies.Count == 2 && held.Observations == 2 && !intent.IsActive && session.State!.Stack.IsEmpty,
                    "Ending the hover pause resumes only the remaining captured effect after its separate presentation beat");
            }
            session.Close();
        }
        var resetConnection = new HeldConnection(reduced);
        var reset = new ClientSession(resetConnection); reset.Accept(initial);
        var resettingIntent = StackResolutionIntent.TryArm(initial)!;
        reset.AdvanceStack(resettingIntent, PresentationPacing.ShouldPresent);
        await resetConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reset.Reset(); resetConnection.Release.TrySetResult(); await Pump(reset);
        check(reset.State == null && !resettingIntent.IsActive && resetConnection.Observations == 1 && !reset.Busy,
            "Reset retires the stack intent and its late publication while reconciling the pass already sent");
        reset.Close();
        var closingConnection = new HeldConnection(reduced);
        var closing = new ClientSession(closingConnection); closing.Accept(initial);
        var closingIntent = StackResolutionIntent.TryArm(initial)!;
        closing.AdvanceStack(closingIntent, PresentationPacing.ShouldPresent);
        await closingConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        closing.Close(); closingConnection.Release.TrySetResult();
        await closingConnection.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(!closingIntent.IsActive && closingConnection.Replies.Count == 1 && closingConnection.Observations == 1,
            "Closing a session retires its stack intent while still observing the result of the already submitted command");
        foreach (bool rejected in new[] { false, true }) {
            var broken = new FailingConnection(rejected);
            var session = new ClientSession(broken); session.Accept(initial);
            var intent = StackResolutionIntent.TryArm(initial)!;
            session.AdvanceStack(intent, PresentationPacing.ShouldPresent); await Pump(session);
            check(!intent.IsActive && broken.Replies == 1 && !session.AdvanceStack(intent, PresentationPacing.ShouldPresent)
                && session.NeedsRecovery == !rejected && session.UncertainCommand == !rejected,
                "A rejected or uncertain stack pass stops its intent without retrying or disguising transport recovery");
            session.Close();
        }
        var normal = new ResolvingConnection(initial);
        var manual = new ClientSession(normal); manual.Accept(initial);
        var manualIntent = StackResolutionIntent.TryArm(initial)!;
        manual.AdvanceStack(manualIntent, PresentationPacing.ShouldPresent); await Pump(manual);
        check(manualIntent.IsActive && manual.Submit(new PassPriority(manual.State!.Id, manual.State.Decision!.Id)) && !manualIntent.IsActive,
            "A valid manual gameplay command ends the previously armed stack intent before taking the shared command lane");
        await Pump(manual); manual.Close();
    }

    private static async Task Pump(ClientSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Busy && DateTime.UtcNow < deadline) { session.Pump(); await Task.Delay(1); }
        session.Pump();
        if (session.Busy) throw new TimeoutException("The controlled stack batch did not release its command lane.");
    }
    private static Decision Priority(string id) => new(id, new(), new PriorityPayload(new("Pass", true), CanAutoPass: false));
    private static GameSnapshot State(int count) => new("stack-game", "playing", null, null, 0, 7, "Main", "MAIN1", 1,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
         new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])],
        Enumerable.Range(0, count).Select(index => new StackItem("Queued effect " + index, "Public rules", StackActionKind.TriggeredAbility, null) {
            Id = "queued-" + index, ControllerId = 0, Targets = [new("player", "0", "You", 0)] }).ToImmutableArray(),
        null, Priority("priority-" + count)) { Revision = 1, StackInterruptionVersion = 9,
            PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority" };

    private sealed class ResolvingConnection(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State { get; private set; } = state;
        internal List<GameCommand> Replies { get; } = [];
        internal int Observations;
        public Task ExecuteAsync(GameCommand command)
        {
            if (command is not PassPriority || !GameCommandPermissions.Allows(State, command))
                throw new InvalidOperationException("Only current ordinary priority passes are allowed.");
            command.Validate(); Replies.Add(command);
            State = State with { Revision = State.Revision + 1, Stack = State.Stack.RemoveAt(0), Decision = Priority("priority-after-" + Replies.Count) };
            return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(State); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class SequenceConnection(params GameSnapshot[] snapshots) : IMatchConnection
    {
        private int index;
        internal int Observations;
        internal List<GameCommand> Replies { get; } = [];
        public Task ExecuteAsync(GameCommand command) { command.Validate(); Replies.Add(command); return Task.CompletedTask; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(snapshots[Math.Min(index++, snapshots.Length - 1)]); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class HeldConnection(GameSnapshot after) : IMatchConnection
    {
        internal GameSnapshot After { get; set; } = after;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<GameCommand> Replies { get; } = [];
        internal int Observations;
        public async Task ExecuteAsync(GameCommand command) { command.Validate(); Replies.Add(command); Started.TrySetResult(); await Release.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; Observed.TrySetResult(); return Task.FromResult<GameSnapshot?>(After); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class FailingConnection(bool rejected) : IMatchConnection
    {
        internal int Replies;
        public Task ExecuteAsync(GameCommand command)
        {
            Replies++;
            if (rejected) throw new EngineCommandRejectedException("Stale stack fence");
            throw new IOException("Lost acknowledgement");
        }
        public Task<GameSnapshot?> ObserveAsync() => throw new InvalidOperationException("An uncertain command cannot retry or assume a result.");
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
