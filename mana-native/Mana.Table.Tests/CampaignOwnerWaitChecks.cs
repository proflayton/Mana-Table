using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

internal static class CampaignOwnerWaitChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "polled-full-game-contracts", "passive-owner-wait");
        Directory.CreateDirectory(directory);
        using var evidence = new AiCampaignDiagnostics(directory);
        var inbox = evidence.EnableOrderedHandoff(8);
        var port = new Port();
        var host = new ClientSession(port);
        var guest = new ClientSession(port);
        var clock = new Clock();

        var pending = inbox.WaitForQueuedWorkAsync();
        inbox.Returned([]);
        inbox.BeginNextCycle();
        check(!pending.IsCompleted && inbox.Status is { Pending: 0, IdleReturnedBatches: 1, IdleProcessedBatches: 1 },
            "Scalar pregame idle accounting creates no queued event or false wake for the campaign owner");
        using var detached = new CancellationTokenSource();
        var canceled = inbox.WaitForQueuedWorkAsync(detached.Token);
        detached.Cancel();
        check(await IsCanceled(canceled) && !pending.IsCompleted,
            "Evidence waiter cancellation detaches only that waiter without retiring the FIFO's shared wake");
        var state = PresentationSmoke.Example();
        await Task.Run(() => inbox.Post(new CampaignEvidenceEvent.Public(state)));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        check(inbox.WaitForQueuedWorkAsync().IsCompletedSuccessfully && inbox.Status is { Pending: 1, Applied: 0 },
            "Queued-before and waited-before evidence notifications wake without draining or mutating collected diagnostics");
        inbox.Drain();
        check(!inbox.WaitForQueuedWorkAsync().IsCompleted && inbox.Status is { Pending: 0, Applied: 1 },
            "An evidence pulse is consumed by its existing serial owner and cannot spin an empty campaign");

        int callbacks = 0;
        host.Post(() => callbacks++);
        await CampaignOwnerWait.WaitAsync(TimeSpan.FromSeconds(15), inbox.WaitForQueuedWorkAsync, host, guest, clock: clock);
        check(callbacks == 0 && clock.PendingTimers == 0 && port.Calls == 0,
            "A campaign wait sees already queued work immediately, cancels its deadline, and never pumps or dispatches it");
        host.Pump();
        var guestWake = CampaignOwnerWait.WaitAsync(TimeSpan.FromSeconds(15), inbox.WaitForQueuedWorkAsync, host, guest, clock: clock);
        check(!guestWake.IsCompleted && clock.PendingTimers == 1, "An idle campaign owns one bounded timer instead of repeatedly scheduling 1 ms sleeps");
        await Task.Run(() => guest.Post(() => callbacks++));
        await guestWake.WaitAsync(TimeSpan.FromSeconds(5));
        check(callbacks == 1 && clock.PendingTimers == 0, "Guest work wakes the one logical owner and cancels its losing timer without pumping the guest");
        guest.Pump();

        var evidenceWake = CampaignOwnerWait.WaitAsync(TimeSpan.FromSeconds(15), inbox.WaitForQueuedWorkAsync, host, guest, clock: clock);
        await Task.Run(() => inbox.Post(new CampaignEvidenceEvent.Public(state)));
        await evidenceWake.WaitAsync(TimeSpan.FromSeconds(5));
        check(inbox.Status is { Pending: 1, Applied: 1 } && clock.PendingTimers == 0,
            "Background evidence wakes an owner waiting on client operations without losing its undrained FIFO record");
        inbox.Drain();

        TimeSpan due = CampaignOwnerWait.Bound(TimeSpan.FromSeconds(180), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(50));
        var poll = CampaignOwnerWait.WaitAsync(due, inbox.WaitForQueuedWorkAsync, host, guest, clock: clock);
        for (int index = 0; index < 49; index++) clock.Advance(TimeSpan.FromMilliseconds(1));
        check(due == TimeSpan.FromMilliseconds(50) && !poll.IsCompleted && clock.PendingTimers == 1 && port.Calls == 0,
            "An idle owner stays asleep until the existing 50 ms ordinary-poll deadline rather than waking or polling every millisecond");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await poll.WaitAsync(TimeSpan.FromSeconds(5));
        check(clock.PendingTimers == 0 && CampaignOwnerWait.Bound(TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(120)) == TimeSpan.FromMilliseconds(7)
                && CampaignOwnerWait.Bound(TimeSpan.FromSeconds(180), TimeSpan.Zero) == TimeSpan.Zero,
            "The earliest real poll or unchanged match deadline releases the owner; a due poll is never delayed by the scheduler");

        var decisionPoll = CampaignOwnerWait.WaitAsync(TimeSpan.FromMilliseconds(120), inbox.WaitForQueuedWorkAsync, host, guest, clock: clock);
        clock.Advance(TimeSpan.FromMilliseconds(119));
        check(!decisionPoll.IsCompleted, "The decision poll cadence remains 120 ms during idle owner waits");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await decisionPoll.WaitAsync(TimeSpan.FromSeconds(5));

        for (int index = 0; index < 128; index++) {
            using var cancellation = new CancellationTokenSource();
            var wait = CampaignOwnerWait.WaitAsync(TimeSpan.FromSeconds(15), inbox.WaitForQueuedWorkAsync, host, guest, cancellation.Token, clock);
            cancellation.Cancel();
            if (!await IsCanceled(wait) || clock.PendingTimers != 0)
                throw new InvalidOperationException("An owner wait retained a canceled queue subscription or deadline.");
        }
        check(port.Calls == 0 && inbox.Status is { Applied: 2, Pending: 0 } && callbacks == 2,
            "Repeated canceled campaign waits retain no timers and cannot become polls, gameplay, evidence application or user callbacks");
        await SynchronizationHealth(inbox, check);
        var retiring = inbox.WaitForQueuedWorkAsync();
        inbox.Retire();
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        check(inbox.WaitForQueuedWorkAsync().IsCompletedSuccessfully, "Evidence retirement releases current and later owner waits without requiring another producer");
        host.Close(); guest.Close();
    }

    private static async Task SynchronizationHealth(AiCampaignEvidenceInbox inbox, Action<bool, string> check)
    {
        var clock = new Clock();
        var state = PresentationSmoke.Example();
        var port = new HeldPort();
        var client = new ClientSession(port, clock: clock);
        client.Accept(state);
        check(client.TimeUntilSynchronizationHealthCheck == null,
            "A synchronized idle session creates no health timer before an ordinary poll starts");
        client.Poll(false, _ => { });
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var due = CampaignOwnerWait.Bound(TimeSpan.FromSeconds(180), null, client.TimeUntilSynchronizationHealthCheck);
        var waking = CampaignOwnerWait.WaitAsync(due, inbox.WaitForQueuedWorkAsync, client, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        check(!waking.IsCompleted && !client.NeedsRecovery && clock.PendingTimers == 1,
            "A held ordinary poll preserves the existing strict greater-than-five-seconds health threshold without an idle spin at equality");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await waking.WaitAsync(TimeSpan.FromSeconds(5));
        check(client.NeedsRecovery && client.TimeUntilSynchronizationHealthCheck == TimeSpan.Zero
                && client.Polling && clock.PendingTimers == 0 && port.Reads == 1,
            "A missing poll completion wakes the passive campaign health check at its original deadline instead of sleeping to the 180 s match cutoff");
        client.Close();
        check(client.TimeUntilSynchronizationHealthCheck == null, "Closing the owner retires its synchronization-health deadline");
        port.Release.TrySetResult(state);
        await port.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> IsCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private sealed class Port : IMatchConnection
    {
        internal int Calls;
        public Task<GameSnapshot?> ObserveAsync() { Calls++; throw new InvalidOperationException("A scheduler cannot poll."); }
        public Task ExecuteAsync(GameCommand command) { Calls++; throw new InvalidOperationException("A scheduler cannot dispatch."); }
        public Task ConcedeAsync(string gameId) { Calls++; throw new InvalidOperationException("A scheduler cannot concede."); }
    }

    private sealed class HeldPort : IMatchConnection
    {
        internal int Reads;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GameSnapshot?> ObserveAsync() {
            Reads++; Started.TrySetResult();
            try { return await Release.Task; }
            finally { Returned.TrySetResult(); }
        }
        public Task ExecuteAsync(GameCommand command) => throw new InvalidOperationException("A health timer cannot dispatch.");
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("A health timer cannot concede.");
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch.AddTicks(ticks); }
        internal int PendingTimers { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(this, callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed) {
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
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) {
                this.clock = clock; Callback = callback; State = state;
                lock (clock.gate) { clock.timers.Add(this); Change(due, period); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) {
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
