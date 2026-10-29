using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class MatchCompletionRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await DelayedLocalReturn(check);
        await ExactTerminalObservation(check);
        await CancellationAndDeadline(check);
    }

    private static async Task DelayedLocalReturn(Action<bool, string> check)
    {
        var initial = State(); var clock = new Clock(); var connection = new Connection(initial);
        var session = new ClientSession(connection, clock: clock); session.Accept(initial);
        var returned = new TaskCompletionSource<GameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RunMatchOperation(async () => {
            await connection.ConcedeAsync(initial.Id);
            returned.SetResult(await MatchCompletion.WaitAsync(connection, initial, clock: clock));
        });
        await Until(() => connection.Reads == 1 && clock.PendingTimers > 0);
        check(connection.Concessions == 1 && session.Busy && !returned.Task.IsCompleted,
            "Local return stays in the existing command lane after concession admission until actual match completion");
        bool overlap = false;
        session.Run(() => { overlap = true; return Task.CompletedTask; }); session.Poll(false, _ => overlap = true);
        check(!overlap && connection.Reads == 1, "A dependent local return creates no nested command lane or competing own-seat poll");
        await Step(connection, clock, initial with { Status = "resolving", Decision = null, Revision = 9 });
        await Step(connection, clock, initial with { Decision = null, Revision = 10, Players = [initial.Viewer! with { Eliminated = true }] });
        check(!returned.Task.IsCompleted, "Neither resolving nor an eliminated viewer ends a match that still reports playing");
        await Step(connection, clock, initial with { Status = "finished", Result = "Defeat", Revision = 11 });
        check(!returned.Task.IsCompleted, "A terminal publication with a lingering decision cannot release dependent local work");
        connection.State = initial with { Status = "finished", Result = "Defeat", Decision = null, Revision = 12 };
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var finished = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => { session.Pump(); return !session.Busy; });
        check(finished.Revision == 12 && finished.Result == "Defeat" && connection.Concessions == 1 && connection.Commands == 0,
            "The terminal barrier returns the provider's actual outcome without replaying concession or answering another seat's decision");
        check(clock.PendingTimers == 0, "A completed local-return barrier retains no observation timer");
    }

    private static async Task ExactTerminalObservation(Action<bool, string> check)
    {
        var initial = State();
        var failed = initial with { Status = "error", Error = "Provider stopped", Decision = null, Revision = 9 };
        check(await MatchCompletion.WaitAsync(new Connection(failed), initial) == failed,
            "An actual terminal provider error permits the existing local exit flow without inventing a game result");
        foreach (var replacement in new GameSnapshot?[] { null, failed with { Id = "replacement" }, failed with { ViewerId = 1 } }) {
            check(await Throws<IOException>(() => MatchCompletion.WaitAsync(new Connection(replacement), initial)),
                "Missing, replaced-game, and foreign-viewer observations cannot complete this local match");
        }
        var clock = new Clock(); var connection = new Connection(failed with { Revision = 7 });
        var wait = MatchCompletion.WaitAsync(connection, initial, clock: clock);
        check(!wait.IsCompleted, "A terminal snapshot older than the captured match cannot release its dependent transition");
        await Step(connection, clock, initial with { Revision = 10, Decision = null });
        await Step(connection, clock, failed with { Revision = 9 });
        check(!wait.IsCompleted, "Later observations advance the revision floor so an intervening stale terminal cannot finish the wait");
        connection.State = failed with { Revision = 11 }; clock.Advance(TimeSpan.FromMilliseconds(50));
        check((await wait.WaitAsync(TimeSpan.FromSeconds(5))).Revision == 11,
            "The exact same-game terminal observation completes after stale publications are discarded");
        var lateClock = new Clock();
        var late = new Connection(failed) { Read = () => { lateClock.Advance(TimeSpan.FromSeconds(31)); return Task.FromResult<GameSnapshot?>(failed); } };
        check(await Throws<TimeoutException>(() => MatchCompletion.WaitAsync(late, initial, clock: lateClock)),
            "A terminal read delivered outside the caller's elapsed budget cannot extend the completion deadline");
    }

    private static async Task CancellationAndDeadline(Action<bool, string> check)
    {
        var initial = State(); var clock = new Clock(); var connection = new Connection(initial);
        using var cancel = new CancellationTokenSource();
        var waiting = MatchCompletion.WaitAsync(connection, initial, clock: clock, cancellation: cancel.Token);
        cancel.Cancel();
        check(await Throws<OperationCanceledException>(() => waiting) && connection.Reads == 1 && clock.PendingTimers == 0,
            "Cancellation stops an observation delay without leaving a timer or issuing another request");

        var read = new TaskCompletionSource<GameSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new Connection(initial) { Read = () => read.Task }; var blockedClock = new Clock();
        var timeout = MatchCompletion.WaitAsync(blocked, initial, clock: blockedClock);
        blockedClock.Advance(TimeSpan.FromSeconds(30));
        check(await Throws<TimeoutException>(() => timeout) && blocked.Reads == 1 && blockedClock.PendingTimers == 0,
            "The default thirty-second budget also bounds an observation whose transport has not replied");
        read.SetResult(initial with { Status = "finished", Decision = null });
        check(timeout.IsFaulted, "A late response cannot turn a timed-out completion barrier into a successful transition");

        using var blockedCancel = new CancellationTokenSource();
        var pending = new TaskCompletionSource<GameSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = MatchCompletion.WaitAsync(new Connection(initial) { Read = () => pending.Task }, initial,
            cancellation: blockedCancel.Token);
        blockedCancel.Cancel();
        check(await Throws<OperationCanceledException>(() => cancelled), "Cancellation also releases a caller waiting for an in-flight observation");
        pending.SetResult(initial);

        var shortClock = new Clock(); var active = new Connection(initial);
        var shortWait = MatchCompletion.WaitAsync(active, initial, TimeSpan.FromMilliseconds(25), shortClock);
        shortClock.Advance(TimeSpan.FromMilliseconds(25));
        check(await Throws<TimeoutException>(() => shortWait) && active.Reads == 1,
            "A caller's remaining watchdog budget is preserved rather than replaced by a fresh thirty seconds");
    }

    private static async Task Step(Connection connection, Clock clock, GameSnapshot state)
    {
        int previous = connection.Reads;
        connection.State = state; clock.Advance(TimeSpan.FromMilliseconds(50));
        await Until(() => connection.Reads > previous && clock.PendingTimers > 0);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(1);
        if (!condition()) throw new TimeoutException("The completion regression did not reach its controlled observation boundary.");
    }

    private static async Task<bool> Throws<T>(Func<Task> operation) where T : Exception
    {
        var task = operation();
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (T) when (task.IsCompleted) { return true; }
    }

    private static GameSnapshot State() => new("local-completion", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("priority", new(), new PriorityPayload(new("Pass", true), null, false, "", []))) { Revision = 8 };

    private sealed class Connection(GameSnapshot? state) : IMatchConnection
    {
        internal volatile GameSnapshot? State = state;
        internal int Reads, Concessions, Commands;
        internal Func<Task<GameSnapshot?>>? Read;
        public Task<GameSnapshot?> ObserveAsync() { Interlocked.Increment(ref Reads); return Read?.Invoke() ?? Task.FromResult(State); }
        public Task ConcedeAsync(string gameId) { Interlocked.Increment(ref Concessions); return Task.CompletedTask; }
        public Task ExecuteAsync(GameCommand command) { Interlocked.Increment(ref Commands); throw new InvalidOperationException("Completion must only observe."); }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        internal int PendingTimers { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(this, callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed)
        {
            List<Timer> ready;
            lock (gate) {
                ticks += elapsed.Ticks;
                ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            private readonly Clock clock;
            internal readonly TimerCallback Callback;
            internal readonly object? State;
            internal long Due;
            internal TimeSpan Period;
            private bool disposed;
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            {
                this.clock = clock; Callback = callback; State = state;
                lock (clock.gate) { clock.timers.Add(this); Change(due, period); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate) {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                    Period = period; return true;
                }
            }
            public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
