using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Mana.Forge;

namespace Mana.Table;

internal static class EngineDiagnosticsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await CheckFailureStages(check);
        await CheckBoundedOutputAndRecovery(check);
        await SharedRetirementBudgetCoversDrainAndSink(check);
        await CheckOrdinaryRetirement(check);
        await CheckAbandonedWriterDoesNotRetainDrain(check);
        foreach (var stage in new[] { EngineDiagnosticStage.Opening, EngineDiagnosticStage.Writing,
            EngineDiagnosticStage.Flushing, EngineDiagnosticStage.Disposing }) await CheckAbandonedStage(stage, check);
    }

    private static async Task CheckFailureStages(Action<bool, string> check)
    {
        foreach (var stage in new[] { EngineDiagnosticStage.Opening, EngineDiagnosticStage.Writing,
            EngineDiagnosticStage.Flushing, EngineDiagnosticStage.Disposing }) {
            var sink = new Sink(fail: stage);
            var source = new StringReader(new string('x', 8192) + ForgeDiagnosticsProtocolProbe.Sentinel);
            var diagnostics = new EngineDiagnostics(1, source,
                () => stage == EngineDiagnosticStage.Opening ? throw new IOException("authored open failure") : sink);
            await diagnostics.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            var terminal = await diagnostics.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(terminal.ReadCharacters == 8192 + ForgeDiagnosticsProtocolProbe.Sentinel.Length
                    && terminal.EndOfStream && terminal.DrainCompleted && terminal.FailureStage == stage
                    && !terminal.RetirementAbandoned && terminal.PendingChunks == 0,
                "A diagnostic " + stage + " failure preserves complete stderr drain and its original sink-stage evidence");
            check(diagnostics.CaptureTail() == "" && ReferenceEquals(terminal, diagnostics.Status)
                    && ReferenceEquals(await diagnostics.RetireAsync(), terminal),
                "A retired " + stage + " owner exposes only its frozen status, with empty private tail and idempotent retirement");
        }
        var brokenReader = new BrokenReader();
        var read = new EngineDiagnostics(2, brokenReader, () => new Sink());
        var readStatus = await read.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(readStatus.ReadFailed && !readStatus.EndOfStream && !readStatus.RetirementAbandoned,
            "A failed stderr reader records diagnostic evidence without converting it to a protocol or sink failure");
    }

    private static async Task CheckBoundedOutputAndRecovery(Action<bool, string> check)
    {
        var sink = new Sink(hold: EngineDiagnosticStage.Writing);
        var diagnostics = new EngineDiagnostics(3,
            new StringReader(new string('z', 2 * 1024 * 1024) + ForgeDiagnosticsProtocolProbe.Sentinel), () => sink);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await diagnostics.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var held = diagnostics.Status;
        check(held.ReadCharacters == 2 * 1024 * 1024 + ForgeDiagnosticsProtocolProbe.Sentinel.Length
                && held.QueueHighWater == EngineDiagnostics.QueueCapacity && held.PendingChunks <= EngineDiagnostics.QueueCapacity
                && held.DroppedCharacters > 0 && diagnostics.CaptureTail().Length == EngineDiagnostics.TailCharacters
                && diagnostics.CaptureTail().EndsWith(ForgeDiagnosticsProtocolProbe.Sentinel, StringComparison.Ordinal),
            "A held sink cannot block 2 MiB of unterminated input; queue/tail remain fixed while recent output and explicit omission counts survive");
        sink.Release.TrySetResult();
        var status = await diagnostics.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(!status.RetirementAbandoned && status.ReadCharacters == status.WrittenCharacters + status.DroppedCharacters
                && sink.Text.Contains("diagnostics omitted", StringComparison.Ordinal)
                && sink.Text.EndsWith(ForgeDiagnosticsProtocolProbe.Sentinel, StringComparison.Ordinal),
            "Releasing a slow sink writes the retained suffix and omission marker, accounts for all input, and finishes normally");
    }

    internal static async Task SharedRetirementBudgetCoversDrainAndSink(Action<bool, string> check)
    {
        var clock = new Clock(); var reader = new GatedReader();
        var sink = new Sink(hold: EngineDiagnosticStage.Writing, failAfterRelease: true);
        var diagnostics = new EngineDiagnostics(4, reader, () => sink, clock);
        await Task.WhenAll(reader.Entered.Task, sink.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = diagnostics.RetireAsync();
        check(clock.PendingTimers == 1, "Diagnostic retirement creates one budget for the held drain and sink together");
        clock.Advance(TimeSpan.FromMilliseconds(300)); reader.Release.TrySetResult();
        await diagnostics.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(199));
        check(!retirement.IsCompleted, "After 300 ms of held drain, the same retirement stays pending at 499 total virtual ms");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var frozen = await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        check(frozen.RetirementAbandoned && frozen.WriterAbandoned && frozen.DrainCompleted
                && clock.PendingTimers == 0 && diagnostics.CaptureTail() == "",
            "The single 500 ms diagnostic budget includes both drain and sink, retires its timer, and clears private text");
        sink.Release.TrySetResult(); await sink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(ReferenceEquals(diagnostics.Status, frozen) && frozen.FailureStage == null
                && diagnostics.CaptureTail() == "" && ReferenceEquals(await diagnostics.RetireAsync(), frozen),
            "Late sink failure and repeated disposed reads cannot mutate frozen evidence, re-open output or restart retirement");
    }

    private static async Task CheckOrdinaryRetirement(Action<bool, string> check)
    {
        var clock = new Clock(); var reader = new GatedReader(); var sink = new Sink(hold: EngineDiagnosticStage.Writing);
        var diagnostics = new EngineDiagnostics(5, reader, () => sink, clock);
        await Task.WhenAll(reader.Entered.Task, sink.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = diagnostics.RetireAsync();
        clock.Advance(TimeSpan.FromMilliseconds(300)); reader.Release.TrySetResult();
        await diagnostics.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(100)); sink.Release.TrySetResult();
        var status = await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        check(!status.RetirementAbandoned && !status.WriterAbandoned && status.EndOfStream
                && status.SinkStage == EngineDiagnosticStage.Complete && clock.PendingTimers == 0,
            "Ordinary drain and sink completion at 400 ms retire within the shared budget without abandonment");
    }

    private static async Task CheckAbandonedStage(EngineDiagnosticStage stage, Action<bool, string> check)
    {
        var clock = new Clock(); var sink = new Sink(hold: stage, synchronousHold: true);
        var entered = stage == EngineDiagnosticStage.Opening ? NewSignal() : sink.Entered;
        var openRelease = NewSignal();
        IEngineDiagnosticSink Open() {
            if (stage == EngineDiagnosticStage.Opening) { entered.TrySetResult(); openRelease.Task.GetAwaiter().GetResult(); }
            return sink;
        }
        var diagnostics = new EngineDiagnostics(6, new StringReader("bounded input"), Open, clock);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await diagnostics.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            var retiring = diagnostics.RetireAsync(); clock.Advance(EngineDiagnostics.RetirementBudget);
            var status = await retiring.WaitAsync(TimeSpan.FromSeconds(5));
            check(status.RetirementAbandoned && status.WriterAbandoned && status.DrainCompleted
                    && status.SinkStage == stage && clock.PendingTimers == 0,
                "A sink blocking synchronously in " + stage + " cannot extend diagnostic retirement beyond its single budget");
        } finally { openRelease.TrySetResult(); sink.Release.TrySetResult(); }
        await sink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task CheckAbandonedWriterDoesNotRetainDrain(Action<bool, string> check)
    {
        var sink = new Sink(hold: EngineDiagnosticStage.Writing);
        var reference = CreateAbandonedOwner(sink);
        try {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            check(!reference.TryGetTarget(out _),
                "An abandoned writer retains its bounded writer state but cannot retain the retired pipe/tail diagnostic owner");
        } finally { sink.Release.TrySetResult(); }
        await sink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Keep JIT local lifetime outside the collection witness. The sole live
    // external object is the controlled sink, which does not reference its owner.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<EngineDiagnostics> CreateAbandonedOwner(Sink sink)
    {
        var clock = new Clock();
        var diagnostics = new EngineDiagnostics(7, new StringReader("private retired tail"), () => sink, clock);
        sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        diagnostics.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var retiring = diagnostics.RetireAsync(); clock.Advance(EngineDiagnostics.RetirementBudget);
        var terminal = retiring.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (!terminal.WriterAbandoned) throw new InvalidOperationException("The ownership witness requires a still-abandoned writer.");
        return new(diagnostics);
    }

    internal static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Until(Func<bool> done, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Waiting for " + description);
            await Task.Delay(5);
        }
    }

    internal sealed class Sink(EngineDiagnosticStage? hold = null, EngineDiagnosticStage? fail = null,
        bool failAfterRelease = false, bool synchronousHold = false) : IEngineDiagnosticSink
    {
        private readonly object gate = new();
        private readonly StringBuilder text = new();
        private int held, writes;
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Release { get; } = NewSignal();
        internal TaskCompletionSource Disposed { get; } = NewSignal();
        internal string Text { get { lock (gate) return text.ToString(); } }
        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellation)
        {
            int attempt = Interlocked.Increment(ref writes);
            if (fail == EngineDiagnosticStage.Writing && attempt == 3)
                throw new IOException("authored write failure after accepted data");
            var wait = Hold(EngineDiagnosticStage.Writing);
            return wait.IsCompletedSuccessfully ? Append(value) : new ValueTask(AppendAfter(wait, value));
        }
        private ValueTask Append(ReadOnlyMemory<char> value) { lock (gate) text.Append(value.Span); return ValueTask.CompletedTask; }
        private async Task AppendAfter(Task wait, ReadOnlyMemory<char> value) { await wait; await Append(value); }
        public ValueTask FlushAsync(CancellationToken cancellation)
        {
            if (fail == EngineDiagnosticStage.Flushing && Volatile.Read(ref writes) >= 2) throw new IOException("authored flush failure");
            return new(Hold(EngineDiagnosticStage.Flushing));
        }
        public ValueTask DisposeAsync()
        {
            var wait = Hold(EngineDiagnosticStage.Disposing);
            return new(FinishDispose(wait));
        }
        private async Task FinishDispose(Task wait)
        {
            try { await wait; if (fail == EngineDiagnosticStage.Disposing) throw new IOException("authored dispose failure"); }
            finally { Disposed.TrySetResult(); }
        }
        private Task Hold(EngineDiagnosticStage stage)
        {
            if (hold != stage || Interlocked.CompareExchange(ref held, 1, 0) != 0) return Task.CompletedTask;
            Entered.TrySetResult();
            if (synchronousHold) { Release.Task.GetAwaiter().GetResult(); return AfterRelease(); }
            return WaitRelease();
        }
        private Task AfterRelease() => failAfterRelease ? Task.FromException(new IOException("authored late sink failure")) : Task.CompletedTask;
        private async Task WaitRelease() { await Release.Task; await AfterRelease(); }
    }

    private sealed class GatedReader : TextReader
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Release { get; } = NewSignal();
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellation = default)
        {
            Entered.TrySetResult(); return new(ReadEnd());
        }
        private async Task<int> ReadEnd() { await Release.Task; return 0; }
    }
    private sealed class BrokenReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellation = default)
            => throw new IOException("authored stderr read failure");
    }

    internal sealed class Clock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch.AddTicks(ticks); }
        internal int PendingTimers { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(this, callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed)
        {
            List<Timer> ready;
            lock (gate) {
                ticks += elapsed.Ticks; ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            private readonly Clock clock; internal readonly TimerCallback Callback; internal readonly object? State;
            internal long Due; internal TimeSpan Period; private bool disposed;
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            {
                this.clock = clock; Callback = callback; State = state;
                lock (clock.gate) { clock.timers.Add(this); Change(due, period); }
            }
            public bool Change(TimeSpan due, TimeSpan period)
            {
                lock (clock.gate) {
                    if (disposed) return false;
                    Due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + due.Ticks;
                    Period = period; return true;
                }
            }
            public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
