using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

// Actual background host and provider boundaries. Authored immutable receipts
// model admission/observation order, not game resolution or policy fulfillment.
internal static class AiHostLoopRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await AcceptedRefresh(check);
        foreach (string mode in new[] { "empty", "resolving", "superseded", "rejected", "unknown", "policy-failure" })
            await IdleOutcome(mode, check);
        foreach (string mode in new[] { "mixed-rejected", "mixed-unknown" }) await MixedOutcome(mode, check);
        await RealPacing(check);
        await AcceptedWatchdog(check);
        await ForeignHumanWait(check);
        await DirectStep(check);
    }

    private static async Task AcceptedRefresh(Action<bool, string> check)
    {
        var clock = new Clock(); var original = Frame(); var fresh = Frame("next-current", 3);
        var provider = new Port(clock) { Reply = read => read switch {
            1 => [original], 2 => [Resolving(original, 1)], 3 => [original], _ => [fresh]
        }, HoldRead = 5 };
        var policy = new Policy(); var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        try {
            await host.StartAsync();
            // The actual idle timer is the completed-loop barrier for both the
            // old unconditional-delay baseline and the new resolving reply.
            await Until(() => clock.IdlePending > 0);
            Console.WriteLine("AI loop first accepted boundary: reads=" + provider.Reads
                + "; idleTimers=" + clock.IdlePending + "; readStarts=" + string.Join(',', provider.ReadStarts));
            check(provider.Reads == 2 && provider.ReadStarts.SequenceEqual([0L, 0L]),
                "An accepted AI submission immediately requests one fresh observation without a fixed idle delay");
            await Until(() => clock.IdlePending == 1);
            check(provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "accepted-current" }]
                    && policy.Calls == 1 && Accepted(host) == 1,
                "The immediate resolving observation does not establish input consumption or grant replay");
            clock.Advance(39);
            check(provider.Reads == 2, "Resolving work keeps the unchanged 40 ms idle delay through 39 ms");
            clock.Advance(1); await Until(() => provider.Reads >= 3 && clock.IdlePending == 1);
            check(provider.Commands.Count == 1 && policy.Calls == 1 && provider.ReadStarts.SequenceEqual([0L, 0L, 40L]),
                "The identical old stamp after a same-revision resolving frame cannot replay its accepted command or request another immediate read");
            clock.Advance(40); await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "accepted-current" }, KeepOpeningHand { DecisionId: "next-current" }]
                    && provider.ReadStarts.SequenceEqual([0L, 0L, 40L, 80L, 80L]) && policy.Calls == 2
                    && Accepted(host) == 2 && host.LastFault == null,
                "Only independently observed fresh authority submits once and receives its own immediate post-ACK observation");
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            provider.Release.TrySetResult();
            check(provider.Commands.Count == 2 && provider.Reads == 5 && clock.Pending == 0 && provider.Reports.IsEmpty,
                "Disposal cancels the held immediate observation, joins the host and leaves no timer, late policy or replay");
        } finally { provider.Release.TrySetResult(); await host.DisposeAsync(); }
    }

    private static async Task IdleOutcome(string mode, Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Frame();
        var provider = new Port(clock) { Mode = mode, Reply = _ => mode switch {
            "empty" => [], "resolving" => [Resolving(initial, 1)], _ => [initial]
        } };
        var policy = new Policy { Fail = mode == "policy-failure" };
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending > 0 || provider.Reads > 1);
        int expectedCommands = mode is "rejected" or "unknown" ? 1 : 0;
        check(clock.IdlePending == 1 && provider.Reads == 1 && provider.Commands.Count == expectedCommands
                && Accepted(host) == 0 && (mode is "unknown" or "policy-failure" ? host.LastFault != null : host.LastFault == null),
            "No accepted submission means ordinary idle/backoff rather than an immediate observation: " + mode);
        clock.Advance(39);
        check(provider.Reads == 1 && provider.Commands.Count == expectedCommands,
            "An idle or unsafe outcome cannot poll early or replay a command before 40 ms: " + mode);
    }

    private static async Task RealPacing(Action<bool, string> check)
    {
        var clock = new Clock(); var announced = Frame() with { State = Frame().State with {
            Stack = [new("Public announcement", "", false, null) { Id = "announced-spell" }]
        } };
        var provider = new Port(clock) { Reply = _ => [announced], HoldRead = 4 };
        var policy = new Policy(); var pacing = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
        await using var host = new AiTableHost(provider, _ => policy, 42, pacing: pacing, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        check(provider.Reads == 1 && provider.Commands.IsEmpty && policy.Calls == 0,
            "The real shipping announcement pacer withholds policy and commands while the AI loop remains on idle cadence");
        clock.Advance(2399); await Until(() => provider.Reads >= 2 && clock.IdlePending == 1);
        check(provider.Commands.IsEmpty && policy.Calls == 0,
            "A fresh observation before the real 2.4 s beat cannot bypass presentation pacing");
        clock.Advance(40); await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(provider.Commands.Count == 1 && policy.Calls == 1 && provider.Reads == 4
                && provider.ReadStarts.ToArray() is [0, 2399, 2439, 2439] && Accepted(host) == 1,
            "After the shipping beat a fresh current command submits once and only its ACK earns an immediate read");
        provider.Release.TrySetResult();
    }

    private static async Task MixedOutcome(string mode, Action<bool, string> check)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Mode = mode, Reply = _ => [Frame(), Frame("other-seat", seat: 1)] };
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending > 0 || provider.Reads > 1);
        check(provider.Reads == 1 && clock.IdlePending == 1 && provider.Commands.Count == 2 && Accepted(host) == 1,
            "A mixed batch containing an accepted command and an unsafe outcome retains idle backoff: " + mode);
        clock.Advance(39);
        check(provider.Reads == 1 && provider.Commands.Count == 2,
            "An acceptance cannot override another seat's rejected or uncertain receipt and spin the host: " + mode);
    }

    private static async Task AcceptedWatchdog(Action<bool, string> check)
    {
        var clock = new Clock(); var provider = new Port(clock); var policy = new Policy();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StartAsync(); await Until(() => provider.Reads >= 2 && clock.IdlePending == 1);
        clock.Advance(44000); await Until(() => provider.Reads >= 3 && clock.IdlePending == 1);
        check(host.LastFault == null && provider.Commands.Count == 1 && Accepted(host) == 1,
            "The immediate ACK refresh and later identical reads do not renew or prematurely expire the existing accepted-input watchdog");
        clock.Advance(1000); await Until(() => provider.Reports.Count == 1 && clock.IdlePending == 1);
        check(host.LastFault?.ReasonCode == "accepted-no-progress" && provider.Commands.Count == 1 && policy.Calls == 1
                && Accepted(host) == 1 && host.Histories.All(history => history.Decisions.All(row => row.Outcome != AiDecisionOutcome.ExecutionUnknown)),
            "The unchanged 45 s accepted-input deadline faults once without replay or laundering the Accepted receipt");
        clock.Advance(40); await Until(() => provider.Reads >= 5 && clock.IdlePending == 1);
        check(provider.Reports.Count == 1 && provider.Commands.Count == 1,
            "A faulted table keeps idle backoff and cannot emit another report or command on its next observation");
    }

    private static async Task DirectStep(Action<bool, string> check)
    {
        var clock = new Clock(); var provider = new Port(clock); var policy = new Policy();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StepAsync();
        check(provider.Reads == 1 && provider.Commands.Count == 1 && clock.Pending == 0,
            "Public StepAsync still performs exactly one observation and at most one command without starting a background refresh");
        await host.StepAsync();
        check(provider.Reads == 2 && provider.Commands.Count == 1 && policy.Calls == 1 && host.LastFault == null,
            "A caller-driven second Step observes independently without replaying the accepted unchanged input");
    }

    private static async Task ForeignHumanWait(Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Frame();
        var human = initial with { State = initial.State with {
            Revision = 2, BoardRevision = 2, Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1, ActivePlayerId = 1,
            Players = [initial.State.Viewer!, new Player(1, "Human", 40, true, false, ImmutableDictionary<string, int>.Empty, [])]
        } };
        var provider = new Port(clock) { Reply = read => read == 1 ? [initial] : [human] };
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        await host.StartAsync(); await Until(() => provider.Reads >= 2 && clock.IdlePending == 1);
        check(provider.Commands.Count == 1 && Accepted(host) == 1 && host.LastFault == null,
            "A fresh human-owned waiting frame retires the bot's accepted-input watchdog without answering for that human");
        clock.Advance(46000); await Until(() => provider.Reads >= 3 && clock.IdlePending == 1);
        check(provider.Commands.Count == 1 && provider.Reports.IsEmpty && host.LastFault == null,
            "Human waiting beyond 45 s remains ordinary idle work rather than a false bot no-progress fault or implicit answer");
    }

    private static int Accepted(AiTableHost host)
        => host.Histories.Sum(history => history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted));
    private static AutomatedSeatObservation Frame(string decision = "accepted-current", long revision = 1, int seat = 0)
        => new(seat, new("ai-loop-game", "playing", null, null, seat, 0, "Pregame", "PREGAME", null,
            [new Player(seat, "Bot", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new(decision, new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = seat
            }, DecisionActionDomain.None, new());
    private static AutomatedSeatObservation Resolving(AutomatedSeatObservation previous, long revision)
        => previous with { State = previous.State with { Revision = revision, BoardRevision = revision,
            Decision = null, DecisionOwnerId = null, ActionClass = "resolving" } };
    private static async Task Until(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, bound.Token);
    }

    private sealed class Policy : IAiPolicy
    {
        private int calls; internal int Calls => Volatile.Read(ref calls); internal bool Fail;
        public AiPolicyDescriptor Descriptor { get; } = new("ai-loop-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref calls);
            if (Fail) throw new InvalidOperationException("controlled policy failure");
            var state = request.Frame.State;
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "keep-current"));
        }
    }
    private sealed class Port(Clock clock) : IAutomatedSeatProvider
    {
        internal Func<int, ImmutableArray<AutomatedSeatObservation>> Reply = _ => [Frame()];
        internal string Mode = ""; internal int HoldRead;
        private int reads; internal int Reads => Volatile.Read(ref reads);
        internal ConcurrentQueue<long> ReadStarts { get; } = new();
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<string> Reports { get; } = new();
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, GameSnapshot> current = new();
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            int read = Interlocked.Increment(ref reads); ReadStarts.Enqueue(clock.Milliseconds);
            if (read == HoldRead) { Held.TrySetResult(); await Release.Task.WaitAsync(cancellation); }
            if (Mode == "superseded") throw new EngineObservationSupersededException();
            var rows = Reply(read); current.Clear(); foreach (var row in rows) current[row.SeatId] = row.State; return rows;
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (!current.TryGetValue(seatId, out var state) || !GameCommandPermissions.Allows(state, command))
                throw new InvalidDataException("The loop fixture received an unauthorized command");
            Commands.Enqueue(command);
            if (Mode == "rejected" || Mode == "mixed-rejected" && seatId == 1) throw new EngineCommandRejectedException("controlled rejection");
            if (Mode == "unknown" || Mode == "mixed-unknown" && seatId == 1) throw new IOException("controlled unknown receipt");
            return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        { Reports.Enqueue(reasonCode); return Task.CompletedTask; }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        internal long Milliseconds { get { lock (gate) return ticks / TimeSpan.TicksPerMillisecond; } }
        internal int Pending { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        internal int IdlePending { get { lock (gate) return timers.Count(timer => timer.Delay == TimeSpan.FromMilliseconds(40) && timer.Due != long.MaxValue); } }
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
            internal long Due; internal TimeSpan Delay, Period; private bool disposed;
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            { this.clock = clock; Callback = callback; State = state; lock (clock.gate) { clock.timers.Add(this); Change(due, period); } }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (clock.gate) { if (disposed) return false; Delay = dueTime; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; Period = period; return true; } }
            public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
