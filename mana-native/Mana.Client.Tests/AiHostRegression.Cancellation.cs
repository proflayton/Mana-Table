using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static partial class AiHostRegression
{
    private enum HeldHostOperation { Observe, Execute, Report }
    private sealed record HeldOperationResult(string Operation, bool CompletedWhileHeld, bool LateWorkSafe,
        bool FreshScopeSafe, int PolicyCalls, int Commands, int Accepted, int Unknown, int Reports, string? Error);

    private static async Task CancellationAsync(Action<bool, string> check)
    {
        var results = new List<HeldOperationResult>();
        foreach (var operation in Enum.GetValues<HeldHostOperation>()) results.Add(await CancelHeldOperation(operation));
        results.Add(await StopHeldObservation(background: true));
        results.Add(await StopHeldObservation(background: false));
        results.Add(await CancelSynchronouslyBlockedObservation());
        // Complete and clean up every held provider before an assertion can
        // throw. The failing baseline therefore cannot strand a host or hide
        // whether the other provider boundaries have the same defect.
        check(results.All(result => result.CompletedWhileHeld && result.LateWorkSafe && result.FreshScopeSafe && result.Error == null),
            "Caller cancellation and host shutdown finish without waiting for an unresponsive provider, inventing acceptance, or replaying late work: "
                + JsonSerializer.Serialize(results));
        foreach (var result in results) {
            check(result.CompletedWhileHeld, result.Operation + ": the host boundary finishes before the provider is released");
            check(result.LateWorkSafe, result.Operation + ": late provider completion grants no new policy work, acceptance or replay");
            check(result.FreshScopeSafe, result.Operation + ": only an independently observed fresh scope can regain authority after cancellation");
        }
        await ProviderOperationDeadline(check);
    }

    private static async Task<HeldOperationResult> CancelHeldOperation(HeldHostOperation operation)
    {
        var provider = new HeldProvider(operation);
        var policy = new CancellationPolicy();
        var host = new AiTableHost(provider, _ => policy, 42);
        using var cancellation = new CancellationTokenSource();
        Task? pending = null;
        bool completedWhileHeld = false, lateWorkSafe = false, freshScopeSafe = false;
        int calls = 0, commands = 0, accepted = 0, unknown = 0, reports = 0;
        string? failure = null;
        try {
            pending = host.StepAsync(cancellation.Token);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            completedWhileHeld = await FinishedWithin(pending, TimeSpan.FromSeconds(2));
            provider.Release.TrySetResult();
            await Drain(pending);
            calls = policy.Calls; commands = provider.Commands.Count; reports = provider.Reports.Count;
            accepted = host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted));
            unknown = host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.ExecutionUnknown));
            lateWorkSafe = operation == HeldHostOperation.Observe
                ? calls == 0 && commands == 0 && accepted == 0 && unknown == 0 && reports == 0 && host.LastFault == null
                : calls == 1 && commands == 1 && accepted == 0 && unknown == 1 && reports <= 1;

            if (operation == HeldHostOperation.Observe) {
                provider.Current = CancellationOpening("fresh-after-cancel", 2);
                await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
                freshScopeSafe = policy.Calls == 1 && provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "fresh-after-cancel" }]
                    && host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted)) == 1;
            } else {
                await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
                freshScopeSafe = provider.Commands.Count == 1 && policy.Calls == 1
                    && host.Histories.All(history => history.Decisions.All(record => record.Outcome != AiDecisionOutcome.Accepted));
            }
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            provider.Release.TrySetResult();
            try { if (pending != null) await Drain(pending); }
            catch (Exception error) { failure = Append(failure, error); }
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { failure = Append(failure, error); }
        }
        return new(operation.ToString(), completedWhileHeld, lateWorkSafe, freshScopeSafe,
            calls, commands, accepted, unknown, reports, failure);
    }

    private static async Task<HeldOperationResult> StopHeldObservation(bool background)
    {
        var provider = new HeldProvider(HeldHostOperation.Observe);
        var policy = new CancellationPolicy();
        var host = new AiTableHost(provider, _ => policy, 42);
        Task? stopping = null;
        Task? manual = null;
        bool completedWhileHeld = false, lateWorkSafe = false;
        string? failure = null;
        try {
            if (background) await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else manual = host.StepAsync();
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = host.DisposeAsync().AsTask();
            completedWhileHeld = await FinishedWithin(stopping, TimeSpan.FromSeconds(2));
            provider.Release.TrySetResult();
            if (manual != null) await Drain(manual);
            await Drain(stopping);
            lateWorkSafe = policy.Calls == 0 && provider.Commands.IsEmpty && provider.Reports.IsEmpty && host.Histories.IsEmpty;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            provider.Release.TrySetResult();
            try { if (manual != null) await Drain(manual); }
            catch (Exception error) { failure = Append(failure, error); }
            try { if (stopping != null) await Drain(stopping); else await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { failure = Append(failure, error); }
        }
        return new(background ? "Start/Dispose-held-observation" : "Step/Dispose-held-observation", completedWhileHeld, lateWorkSafe, lateWorkSafe,
            policy.Calls, provider.Commands.Count, 0, 0, provider.Reports.Count, failure);
    }

    private static async Task<bool> FinishedWithin(Task task, TimeSpan guard)
    {
        return ReferenceEquals(await Task.WhenAny(task, Task.Delay(guard)), task);
    }

    private static async Task<HeldOperationResult> CancelSynchronouslyBlockedObservation()
    {
        var provider = new SynchronouslyBlockedProvider();
        var policy = new CancellationPolicy();
        var host = new AiTableHost(provider, _ => policy, 42);
        using var cancellation = new CancellationTokenSource();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? caller = null;
        bool completedWhileHeld = false, lateWorkSafe = false, freshScopeSafe = false;
        int calls = 0, commands = 0, accepted = 0, unknown = 0;
        string? failure = null;
        try {
            // Own the caller thread too: a regression that invokes a blocking
            // provider inline must fail a bounded check instead of hanging the
            // test before it can cancel. The returned-task witness separately
            // proves StepAsync itself stopped synchronously occupying its caller.
            caller = Task.Run(async () => {
                var pending = host.StepAsync(cancellation.Token);
                returned.TrySetResult(pending);
                await pending;
            });
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool returnedWhileHeld = await FinishedWithin(returned.Task, TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            bool cancelledWhileHeld = await FinishedWithin(caller, TimeSpan.FromSeconds(2));
            completedWhileHeld = returnedWhileHeld && cancelledWhileHeld;
            provider.Release.Set();
            await provider.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain(caller);
            calls = policy.Calls; commands = provider.Commands.Count;
            accepted = host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted));
            unknown = host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.ExecutionUnknown));
            lateWorkSafe = calls == 0 && commands == 0 && accepted == 0 && unknown == 0 && provider.Reports.IsEmpty && host.LastFault == null;
            provider.Current = CancellationOpening("fresh-after-synchronous-cancel", 2);
            await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
            freshScopeSafe = policy.Calls == 1 && provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "fresh-after-synchronous-cancel" }]
                && host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted)) == 1;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            provider.Release.Set();
            try { if (provider.Entered.Task.IsCompleted) await provider.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { failure = Append(failure, error); }
            try { if (caller != null) await Drain(caller); }
            catch (Exception error) { failure = Append(failure, error); }
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { failure = Append(failure, error); }
            if ((caller == null || caller.IsCompleted) && (!provider.Entered.Task.IsCompleted || provider.Exited.Task.IsCompleted))
                provider.Release.Dispose();
        }
        return new("Synchronously-blocked-observe-before-Task", completedWhileHeld, lateWorkSafe, freshScopeSafe,
            calls, commands, accepted, unknown, provider.Reports.Count, failure);
    }

    private static async Task ProviderOperationDeadline(Action<bool, string> check)
    {
        var provider = new HeldProvider(HeldHostOperation.Observe);
        var policy = new CancellationPolicy();
        var clock = new ProviderDeadlineClock();
        var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        Task? pending = null;
        bool early = true, completedWhileHeld = false, timedOut = false, lateWorkSafe = false, freshScopeSafe = false;
        string? failure = null;
        try {
            pending = host.StepAsync();
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(44));
            early = pending.IsCompleted;
            clock.Advance(TimeSpan.FromSeconds(1));
            completedWhileHeld = await FinishedWithin(pending, TimeSpan.FromSeconds(2));
            if (completedWhileHeld) {
                try { await pending; }
                catch (TimeoutException) { timedOut = true; }
            }
            provider.Release.TrySetResult();
            try { await Drain(pending); }
            catch (TimeoutException) when (timedOut) { }
            lateWorkSafe = policy.Calls == 0 && provider.Commands.IsEmpty && provider.Reports.IsEmpty
                && host.LastFault == null && host.Histories.IsEmpty;
            provider.Current = CancellationOpening("fresh-after-provider-deadline", 2);
            await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
            freshScopeSafe = policy.Calls == 1 && provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "fresh-after-provider-deadline" }]
                && host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted)) == 1;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            provider.Release.TrySetResult();
            try { if (pending != null) await Drain(pending); }
            catch (TimeoutException) when (timedOut) { }
            catch (Exception error) { failure = Append(failure, error); }
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { failure = Append(failure, error); }
        }
        check(!early && completedWhileHeld && timedOut && lateWorkSafe && freshScopeSafe && failure == null,
            "The independent45s provider deadline throws an explicit observation TimeoutException, discards late authority and allows only a fresh observed scope; "
                + JsonSerializer.Serialize(new { early, completedWhileHeld, timedOut, lateWorkSafe, freshScopeSafe, policy.Calls, failure }));
    }
    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (task.IsCanceled) { }
    }
    private static string Append(string? previous, Exception error) => (previous == null ? "" : previous + "\n") + error;

    private static AutomatedSeatObservation CancellationOpening(string decision = "held-current", long revision = 1)
        => new(0, new GameSnapshot("cancelled-host", "playing", null, null, 0, 0, "Pregame", "PREGAME", null,
            [new Player(0, "Bot", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 7, [new Card { VisualId = "owned-opening-face", Name = "Known own face" }], null)])], [], null,
            new(decision, new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0
            }, DecisionActionDomain.None, new());

    private sealed class CancellationPolicy : IAiPolicy
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public AiPolicyDescriptor Descriptor { get; } = new("held-provider-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<AiProposal>(new AiProposal.Answer(
                new KeepOpeningHand(request.Frame.State.Id, request.Frame.State.Decision!.Id), "keep-current"));
        }
    }

    // Deliberately ignores its token. The host must still cancel its own wait
    // and retire an abandoned result instead of relying on provider cooperation.
    private sealed class HeldProvider(HeldHostOperation operation) : IAutomatedSeatProvider
    {
        internal AutomatedSeatObservation Current = CancellationOpening();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<string> Reports { get; } = new();
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            if (operation == HeldHostOperation.Observe) { Entered.TrySetResult(); await Release.Task; }
            return [Current];
        }
        public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (seatId != Current.SeatId || !GameCommandPermissions.Allows(Current.State, command))
                throw new InvalidDataException("The cancellation fixture received an unauthorized command.");
            Commands.Enqueue(command);
            if (operation == HeldHostOperation.Execute) { Entered.TrySetResult(); await Release.Task; }
            if (operation == HeldHostOperation.Report) throw new IOException("Controlled ambiguous execution receipt.");
        }
        public async Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        {
            if (gameId != Current.State.Id || seatId != Current.SeatId) throw new InvalidDataException("Foreign cancellation fault audience.");
            Reports.Enqueue(reasonCode);
            if (operation == HeldHostOperation.Report) { Entered.TrySetResult(); await Release.Task; }
        }
    }

    private sealed class SynchronouslyBlockedProvider : IAutomatedSeatProvider
    {
        internal AutomatedSeatObservation Current = CancellationOpening();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<string> Reports { get; } = new();
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            Entered.TrySetResult();
            Release.Wait(); // Intentionally blocks BEFORE returning any Task and ignores cancellation.
            Exited.TrySetResult();
            return Task.FromResult<ImmutableArray<AutomatedSeatObservation>>([Current]);
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (seatId != Current.SeatId || !GameCommandPermissions.Allows(Current.State, command))
                throw new InvalidDataException("The synchronous cancellation fixture received an unauthorized command.");
            Commands.Enqueue(command); return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        {
            Reports.Enqueue(reasonCode); return Task.CompletedTask;
        }
    }

    // Same timer-capable test-clock pattern as MatchCompletionRegression; it
    // advances only provider-operation deadlines, never policy thinking time.
    private sealed class ProviderDeadlineClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<DeadlineTimer> timers = [];
        private long ticks;
        internal TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state, dueTime, period);
            TimerCreated.TrySetResult(); return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            List<DeadlineTimer> ready;
            lock (gate) {
                ticks += elapsed.Ticks;
                ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class DeadlineTimer : ITimer
        {
            private readonly ProviderDeadlineClock clock;
            internal readonly TimerCallback Callback;
            internal readonly object? State;
            internal long Due;
            internal TimeSpan Period;
            private bool disposed;
            internal DeadlineTimer(ProviderDeadlineClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
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
