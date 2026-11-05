using System.Collections.Immutable;
using System.Collections.Concurrent;
using Mana.Client;
using Mana.Contracts;

// Real finite intents and the ordinary serial client APIs; authored transport
// replies test scheduling/ownership, not Magic resolution or policy fulfillment.
internal static class FiniteServerObservationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ReadBoundaries(check);
        await SliceBoundary(check);
        await IneligibleObservations(check);
        await ImmediateChoices(check);
        await PassiveReadReturnsPayment(check);
        foreach (string retirement in new[] { "hold", "reset", "close" }) await Lifetime(retirement, check);
        ClosedPayloads(check);
    }

    private static GameSnapshot Initial(bool supported = true)
        => FiniteResolutionRegression.State(3) with {
            FiniteStackYield = supported ? new("", FiniteStackYieldStatus.Idle, [], [], 1, 1) : null
        };
    private static FiniteResolutionIntent Arm(GameSnapshot state)
        => FiniteResolutionRegression.Arm(state) ?? throw new InvalidOperationException("Invalid finite scheduling fixture");

    private static async Task ReadBoundaries(Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Initial(); var intent = Arm(initial);
        var port = new Port(initial, clock) { AdvanceOneOnSecondRead = true };
        var advance = new DecisionAdvance(port, clock);
        using var stop = new CancellationTokenSource();
        var run = advance.RunFiniteAsync(initial, intent, (_, _) => port.Reads >= 3, null, stop.Token);
        try {
            await Until(() => clock.Pending > 0 || port.Reads > 1 || run.IsCompleted);
            check(port.Reads == 1 && clock.Pending == 1 && port.Commands is [GrantFiniteStackYield]
                    && port.ReadStarts.SequenceEqual([0L]),
                "An actual finite grant ACK anchors a passive50ms wait instead of starting a second immediate full observation");
            clock.Advance(49);
            check(port.Reads == 1 && !run.IsCompleted,
                "At49ms an acknowledged owned server grant cannot start another full observation");
            clock.Advance(1); await Until(() => port.Reads >= 2 && clock.Pending > 0 || run.IsCompleted);
            check(port.Reads == 2 && port.ReadStarts.SequenceEqual([0L, 50L]) && intent.Progress.CompletedCount == 1,
                "Exactly50ms permits one read and admits its real terminal receipt without changing the original grant scope");
            clock.Advance(49);
            check(port.Reads == 2, "A newer revision and shrinking remaining queue cannot reset the original instruction's read anchor");
            clock.Advance(1); await run.WaitAsync(TimeSpan.FromSeconds(5));
            check(port.ReadStarts.SequenceEqual([0L, 50L, 100L]) && port.Commands is [GrantFiniteStackYield],
                "Stable original members retain50ms start-to-start cadence across admitted progress without local pass commands");
        } finally { stop.Cancel(); await Settle(run); }
    }

    private static async Task SliceBoundary(Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Initial(); var intent = Arm(initial);
        var port = new Port(initial, clock) { HoldFirstRead = true };
        var advance = new DecisionAdvance(port, clock);
        using var stop = new CancellationTokenSource();
        var first = advance.RunFiniteAsync(initial, intent, (_, _) => false, null, stop.Token);
        try {
            await port.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(299); port.ReleaseFirst.TrySetResult();
            await Until(() => port.Reads >= 2 && clock.Pending > 0 || first.IsCompleted);
            check(port.ReadStarts.SequenceEqual([0L, 299L]),
                "A slow command readback stays immediate and does not add a second50ms delay after its elapsed start interval");
            clock.Advance(50); await Until(() => port.Reads >= 3 && clock.Pending > 0 || first.IsCompleted);
            check(port.ReadStarts.SequenceEqual([0L, 299L, 349L]), "The last permitted read before a350ms slice starts at349ms");
            clock.Advance(1); var current = await first.WaitAsync(TimeSpan.FromSeconds(5));
            check(current != null && port.Reads == 3 && clock.Pending == 0,
                "The350ms slice yields during a passive wait without starting another provider read");
            var second = advance.RunFiniteAsync(current!, intent, (_, _) => port.Reads >= 4, null, stop.Token);
            await Until(() => clock.Pending > 0 || port.Reads > 3 || second.IsCompleted);
            clock.Advance(48);
            check(port.Reads == 3, "A new worker slice retains the349ms read anchor rather than renewing an immediate read");
            clock.Advance(1); await second.WaitAsync(TimeSpan.FromSeconds(5));
            check(port.ReadStarts.SequenceEqual([0L, 299L, 349L, 399L]),
                "The next bounded slice reads at399ms, exactly50ms after the prior slice's last start");
        } finally { stop.Cancel(); port.ReleaseFirst.TrySetResult(); await Settle(first); }
    }

    private static async Task IneligibleObservations(Action<bool, string> check)
    {
        foreach (string mode in new[] { "prospective", "foreign", "revoked", "missing", "legacy" }) {
            var clock = new Clock(); var initial = Initial(mode != "legacy"); var intent = Arm(initial);
            var port = new Port(initial, clock) { GrantReply = mode };
            using var stop = new CancellationTokenSource();
            var run = new DecisionAdvance(port, clock).RunFiniteAsync(initial, intent,
                (_, _) => port.Reads >= (mode is "prospective" ? 2 : 1), null, stop.Token);
            try {
                await Until(() => run.IsCompleted || clock.Pending > 0);
                check(run.IsCompleted && clock.Pending == 0
                        && (mode == "legacy" ? port.Commands is [PassPriority] : port.Commands is [GrantFiniteStackYield]),
                    "Only an actually acknowledged owned Active grant opts into passive pacing; excluded scope=" + mode);
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            } finally { stop.Cancel(); await Settle(run); }
        }
    }

    private static async Task ImmediateChoices(Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Initial(); var intent = Arm(initial);
        var port = new Port(initial, clock);
        var runner = new DecisionAdvance(port, clock);
        var acknowledged = await runner.RunFiniteAsync(initial, intent, (_, _) => port.Reads == 1, null);
        clock.Advance(49);
        var payment = FiniteResolutionRegression.State(3, paying: true, revision: 3) with {
            FiniteStackYield = acknowledged!.FiniteStackYield,
            Decision = FiniteResolutionRegression.State(3, paying: true).Decision! with {
                Payload = PaymentPayload()
            }
        };
        port.State = payment;
        var receipt = await runner.RunFiniteAsync(payment, intent, (_, _) => port.Reads >= 2, null);
        check(port.Commands is [GrantFiniteStackYield, CancelPayment] && port.ReadStarts.SequenceEqual([0L, 49L])
                && ReferenceEquals(receipt, port.State) && clock.Pending == 0,
            "A fresh certified required negative-payment command and its full readback bypass passive server-progress pacing");

        var otherInitial = Initial(); var otherIntent = Arm(otherInitial); var otherPort = new Port(otherInitial, clock);
        var otherRunner = new DecisionAdvance(otherPort, clock);
        var active = await otherRunner.RunFiniteAsync(otherInitial, otherIntent, (_, _) => otherPort.Reads == 1, null);
        var required = active! with { Revision = active.Revision + 1, Decision = new("required-number", new(), new NumberPayload("", new(0, 3))) };
        var stopped = await otherRunner.RunFiniteAsync(required, otherIntent, (_, _) => false, null);
        check(ReferenceEquals(stopped, required) && !otherIntent.IsActive && otherPort.Reads == 1 && clock.Pending == 0,
            "An unconsented fresh required choice stops before any passive delay, observation or guessed answer");
    }

    private static PaymentPayload PaymentPayload()
        => ((PaymentPayload)FiniteResolutionRegression.State(3, paying: true).Decision!.Payload) with {
            UnpaidManaParts = [new("{1}", 1)],
            OptionalPaymentSupport = new("fixed-one", [new("{1}", 1)], OptionalManaPaymentAvailability.Unpayable, false)
        };

    private static async Task PassiveReadReturnsPayment(Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Initial(); var intent = Arm(initial);
        var port = new Port(initial, clock) { PaymentOnSecondRead = true };
        var context = new ContinuationQueue();
        var runner = new DecisionAdvance(port, clock);
        using var stop = new CancellationTokenSource();
        var run = context.Enter(() => runner.RunFiniteAsync(initial, intent, (_, _) => port.Reads >= 3, null, stop.Token));
        try {
            check(clock.Pending == 1 && port.Reads == 1, "The passive-payment control begins with the real acknowledged-grant wait");
            clock.Advance(50);
            // Pump only the continuation already made ready by the fake timer.
            // A newly introduced System10ms wait would leave this work unfinished,
            // even if its callback later posts another continuation.
            if (!run.IsCompleted) context.DrainOne();
            check(run.IsCompletedSuccessfully && port.Commands is [GrantFiniteStackYield, CancelPayment]
                    && port.ReadStarts.SequenceEqual([0L, 50L, 50L]) && clock.Pending == 0,
                "A payment returned by a passive read dispatches its certified Cancel and immediate readback in that same continuation, without a new10ms or50ms wait");
            await run;
        } finally {
            stop.Cancel();
            await Until(() => run.IsCompleted, context.DrainAll);
            await Settle(run);
        }
    }

    private static async Task Lifetime(string retirement, Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Initial(); var port = new Port(initial, clock);
        var session = new ClientSession(port, clock: clock); session.Accept(initial); var intent = Arm(initial);
        int failures = 0; session.Failed += _ => failures++;
        check(session.AdvanceFinite(intent, (_, _) => false), "A real session starts the pending server wait before " + retirement);
        try {
            await Until(() => clock.Pending > 0 || port.Reads > 1, session.Pump);
            check(clock.Pending == 1 && port.Reads == 1 && port.Commands is [GrantFiniteStackYield],
                "The actual client owner reaches an acknowledged-grant timer without a second full read before " + retirement);
            if (retirement == "hold") {
                clock.Advance(349);
                await Until(() => port.Reads >= 2 && clock.Pending > 0, session.Pump);
                check(port.ReadStarts.SequenceEqual([0L, 349L]),
                    "Hold is exercised during the final1ms slice-capped wait after a349ms passive read");
            }
            int readsBeforeCleanup = port.Reads;
            long timeBeforeCleanup = clock.ElapsedMilliseconds;
            if (retirement == "hold") { intent.Pause(); session.CancelAdvance(); }
            else if (retirement == "reset") session.Reset();
            else session.Close();
            await Until(() => port.RevokeRead.Task.IsCompleted && (retirement == "close" || !session.Busy),
                retirement == "close" ? null : session.Pump);
            check(clock.ElapsedMilliseconds == timeBeforeCleanup && clock.Pending == 0 && port.Commands is [GrantFiniteStackYield, RevokeFiniteStackYield]
                    && port.Reads == readsBeforeCleanup + 1 && failures == 0 && !session.NeedsRecovery && !session.UncertainCommand,
                "Cancelling " + retirement + " releases the passive timer immediately and serially fences its exact grant with one negative command/readback");
            check(retirement != "reset" || session.State == null,
                "Negative acknowledgement after " + retirement + " cannot revive a reset gameplay replica");
            check(retirement != "close" || session.Closed,
                "Closed ownership stays closed after its unpumped negative cleanup");
        } finally { session.Close(); }
    }

    // Kept outside the async entry state machine so the first timing control
    // can run against the old assembly's unchanged RunFiniteAsync ABI.
    private static void ClosedPayloads(Action<bool, string> check)
        => check(Rejects(() => new FiniteNextStep.DispatchCommand(null!))
                && Rejects(() => new FiniteNextStep.AwaitAcknowledgedServerProgress(null!)),
            "Closed finite steps reject missing command or acknowledged-server scope payloads");

    private static bool Rejects(Action action) { try { action(); return false; } catch (ArgumentNullException) { return true; } }
    private static async Task Until(Func<bool> condition, Action? pump = null)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { bound.Token.ThrowIfCancellationRequested(); pump?.Invoke(); await Task.Delay(1, bound.Token); }
        pump?.Invoke();
    }
    private static async Task Settle(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }

    private sealed class Port(GameSnapshot initial, Clock clock) : IMatchConnection
    {
        internal GameSnapshot State = initial;
        internal readonly List<GameCommand> Commands = [];
        internal readonly List<long> ReadStarts = [];
        internal int Reads;
        internal bool HoldFirstRead, AdvanceOneOnSecondRead, PaymentOnSecondRead;
        internal string GrantReply = "active";
        internal TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RevokeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(State, command)) throw new EngineCommandRejectedException("Invalid current scheduling fixture command");
            Commands.Add(command);
            if (command is GrantFiniteStackYield grant) State = State with { Revision = State.Revision + 1,
                FiniteStackYield = GrantReply == "missing" ? null : GrantReply == "prospective" ? State.FiniteStackYield
                    : new(GrantReply == "foreign" ? "other-owner" : grant.GrantId,
                        GrantReply == "revoked" ? FiniteStackYieldStatus.Revoked : FiniteStackYieldStatus.Active,
                        grant.ExecutionIds, grant.ExecutionIds, grant.StackActionVersion, grant.ResolutionChoiceVersion) };
            else if (command is RevokeFiniteStackYield) State = State with { Revision = State.Revision + 1,
                FiniteStackYield = State.FiniteStackYield! with { Status = FiniteStackYieldStatus.Revoked } };
            else if (command is CancelPayment) State = FiniteResolutionRegression.State(3, completed: 1, revision: State.Revision + 1) with {
                FiniteStackYield = State.FiniteStackYield! with { RemainingExecutionIds = State.FiniteStackYield.RemainingExecutionIds.RemoveAt(0) }
            };
            return Task.CompletedTask;
        }
        public async Task<GameSnapshot?> ObserveAsync()
        {
            int number = ++Reads; ReadStarts.Add(clock.ElapsedMilliseconds);
            if (number == 1) { FirstRead.TrySetResult(); if (HoldFirstRead) await ReleaseFirst.Task; }
            if (AdvanceOneOnSecondRead && number == 2) State = FiniteResolutionRegression.State(3, completed: 1, revision: State.Revision + 1) with {
                FiniteStackYield = State.FiniteStackYield! with { RemainingExecutionIds = State.FiniteStackYield.RemainingExecutionIds.RemoveAt(0), AutomaticPasses = 1 }
            };
            if (PaymentOnSecondRead && number == 2) State = FiniteResolutionRegression.State(3, paying: true, revision: State.Revision + 1) with {
                FiniteStackYield = State.FiniteStackYield,
                Decision = FiniteResolutionRegression.State(3, paying: true).Decision! with { Payload = PaymentPayload() }
            };
            if (State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked) RevokeRead.TrySetResult();
            return State;
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("No scheduling control concedes");
    }

    // A one-continuation pump distinguishes an immediate post-read command from
    // scheduling a new delay. It does not advance clocks or supply policy steps.
    private sealed class ContinuationQueue : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> ready = new();
        public override void Post(SendOrPostCallback callback, object? state) => ready.Enqueue((callback, state));
        internal T Enter<T>(Func<T> body)
        { var before = Current; SetSynchronizationContext(this); try { return body(); } finally { SetSynchronizationContext(before); } }
        internal void DrainOne()
        { if (!ready.TryDequeue(out var item)) throw new InvalidOperationException("Expected a timer-ready continuation"); Enter(() => { item.Callback(item.State); return true; }); }
        internal void DrainAll() { while (!ready.IsEmpty) DrainOne(); }
    }

    // Same bounded timer-capable clock pattern as MatchCompletionRegression.
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        internal long ElapsedMilliseconds { get { lock (gate) return ticks / TimeSpan.TicksPerMillisecond; } }
        internal int Pending { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(this, callback, state, dueTime, period);
        internal void Advance(int milliseconds)
        {
            List<Timer> ready;
            lock (gate) { ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks; ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            private readonly Clock clock; internal readonly TimerCallback Callback; internal readonly object? State;
            internal long Due; internal TimeSpan Period; private bool disposed;
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            { this.clock = clock; Callback = callback; State = state; lock (clock.gate) { clock.timers.Add(this); Change(due, period); } }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (clock.gate) { if (disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; Period = period; return true; } }
            public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
