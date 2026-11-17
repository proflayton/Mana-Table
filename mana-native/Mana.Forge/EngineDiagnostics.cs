using System.Text;

namespace Mana.Forge;

internal enum EngineDiagnosticStage { Suppressed, Opening, Writing, Flushing, Disposing, Complete }
internal sealed record EngineDiagnosticStatus(int ProcessId, long ReadCharacters, long WrittenCharacters,
    long DroppedCharacters, int PendingChunks, int QueueHighWater, bool EndOfStream, bool ReadFailed,
    EngineDiagnosticStage SinkStage, EngineDiagnosticStage? FailureStage, bool CleanupFailed,
    bool LoggingSuppressed, bool RetirementAbandoned, bool WriterAbandoned, bool DrainCompleted);

// Only this small sink is injected. It never sees a process, protocol request,
// rules receipt or diagnostic owner. Its complete lifecycle stays on one worker.
internal interface IEngineDiagnosticSink : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellation);
    ValueTask FlushAsync(CancellationToken cancellation);
}

internal sealed class EngineDiagnostics
{
    internal const int ChunkCharacters = 2048, QueueCapacity = 16, TailCharacters = 32768;
    internal static readonly TimeSpan RetirementBudget = TimeSpan.FromMilliseconds(500);
    private readonly int processId;
    private readonly TextReader reader;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource reading = new();
    private readonly object gate = new();
    private readonly char[] tail = new char[TailCharacters];
    private int tailStart, tailLength;
    private long readCharacters;
    private bool eof, readFailed;
    private readonly WriterState? writer;
    private readonly Task drain;
    private Task<EngineDiagnosticStatus>? retirement;
    private EngineDiagnosticStatus? retired;

    internal EngineDiagnostics(int processId, TextReader reader, Func<IEngineDiagnosticSink>? open,
        TimeProvider? time = null)
    {
        this.processId = processId; this.reader = reader; this.time = time ?? TimeProvider.System;
        // A writer retains only WriterState, never this owner or its pipe/tail.
        writer = open == null ? null : new WriterState(processId, open);
        drain = Task.Run(DrainAsync);
    }

    internal Task DrainCompletion => drain;
    internal EngineDiagnosticStatus Status { get { lock (gate) return retired ?? Capture(false); } }
    internal string CaptureTail()
    {
        lock (gate) {
            if (retired != null || tailLength == 0) return "";
            return string.Create(tailLength, this, (target, owner) => {
                int first = Math.Min(owner.tailLength, TailCharacters - owner.tailStart);
                owner.tail.AsSpan(owner.tailStart, first).CopyTo(target);
                owner.tail.AsSpan(0, owner.tailLength - first).CopyTo(target[first..]);
            });
        }
    }

    private async Task DrainAsync()
    {
        var buffer = new char[ChunkCharacters];
        try {
            while (true) {
                int count = await reader.ReadAsync(buffer.AsMemory(), reading.Token).ConfigureAwait(false);
                if (count == 0) { lock (gate) if (retired == null) eof = true; break; }
                lock (gate) {
                    if (retired != null) break;
                    readCharacters += count;
                    for (int index = 0; index < count; index++) {
                        if (tailLength < TailCharacters) tail[(tailStart + tailLength++) % TailCharacters] = buffer[index];
                        else { tail[tailStart] = buffer[index]; tailStart = (tailStart + 1) % TailCharacters; }
                    }
                }
                writer?.Offer(buffer.AsSpan(0, count));
            }
        } catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception) { lock (gate) if (retired == null) readFailed = true; }
        finally { writer?.Complete(); }
    }

    internal Task<EngineDiagnosticStatus> RetireAsync()
    {
        lock (gate) return retirement ??= RetireCoreAsync();
    }

    private async Task<EngineDiagnosticStatus> RetireCoreAsync()
    {
        using var deadline = new CancellationTokenSource(RetirementBudget, time);
        bool abandoned = false;
        try { await Task.WhenAll(drain, writer?.Completion ?? Task.CompletedTask).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
            abandoned = true;
            // CancelAsync keeps an uncooperative sink cancellation callback off
            // the retirement caller too. Neither late completion owns play.
            Observe(reading.CancelAsync());
            writer?.Abandon();
        }
        lock (gate) {
            retired = Capture(abandoned);
            Array.Clear(tail); tailStart = tailLength = 0;
            return retired;
        }
    }

    private EngineDiagnosticStatus Capture(bool abandoned)
    {
        var output = writer?.Snapshot();
        return new(processId, readCharacters, output?.Written ?? 0, output?.Dropped ?? 0,
            output?.Pending ?? 0, output?.HighWater ?? 0, eof, readFailed,
            output?.Stage ?? EngineDiagnosticStage.Suppressed, output?.Failure, output?.CleanupFailed ?? false,
            writer == null, abandoned, abandoned && writer?.Completion.IsCompleted == false, drain.IsCompleted);
    }

    private static void Observe(Task work)
        => _ = work.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static IEngineDiagnosticSink OpenFile(string path) => new FileSink(path);
    private sealed class FileSink : IEngineDiagnosticSink
    {
        private readonly StreamWriter output;
        internal FileSink(string path)
        {
            var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write,
                Share = FileShare.Read, Options = FileOptions.Asynchronous, BufferSize = 4096 });
            try { output = new(file, new UTF8Encoding(false), 4096); }
            catch { file.Dispose(); throw; }
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellation)
            => await output.WriteAsync(value, cancellation).ConfigureAwait(false);
        public async ValueTask FlushAsync(CancellationToken cancellation)
            => await output.FlushAsync(cancellation).ConfigureAwait(false);
        public ValueTask DisposeAsync() => output.DisposeAsync();
    }

    private sealed record WriterSnapshot(long Written, long Dropped, int Pending, int HighWater,
        EngineDiagnosticStage Stage, EngineDiagnosticStage? Failure, bool CleanupFailed);

    private sealed class WriterState
    {
        private readonly object gate = new();
        private readonly char[][] queued = Enumerable.Range(0, QueueCapacity).Select(_ => new char[ChunkCharacters]).ToArray();
        private readonly int[] lengths = new int[QueueCapacity];
        private readonly char[] buffer = new char[ChunkCharacters];
        private readonly SemaphoreSlim work = new(0, 1);
        private readonly CancellationTokenSource stopping = new();
        private readonly Func<IEngineDiagnosticSink> open;
        private readonly int processId;
        private int head, count, highWater;
        private long written, dropped, reportedDropped;
        private bool complete, disabled, cleanupFailed;
        private EngineDiagnosticStage stage = EngineDiagnosticStage.Opening;
        private EngineDiagnosticStage? failure;
        internal Task Completion { get; }
        internal WriterState(int processId, Func<IEngineDiagnosticSink> open)
        {
            this.processId = processId; this.open = open;
            Completion = Task.Run(RunAsync);
        }
        internal WriterSnapshot Snapshot() { lock (gate) return new(written, dropped, count, highWater, stage, failure, cleanupFailed); }
        internal void Offer(ReadOnlySpan<char> value)
        {
            lock (gate) {
                if (disabled) { dropped += value.Length; return; }
                if (complete) return;
                if (count == QueueCapacity) { dropped += lengths[head]; head = (head + 1) % QueueCapacity; count--; }
                int next = (head + count) % QueueCapacity;
                value.CopyTo(queued[next]); lengths[next] = value.Length;
                highWater = Math.Max(highWater, ++count);
                Wake();
            }
        }
        internal void Complete() { lock (gate) { complete = true; Wake(); } }
        internal void Abandon()
        {
            lock (gate) { disabled = true; DropQueued(); Wake(); }
            Observe(stopping.CancelAsync());
        }
        private void Wake() { if (work.CurrentCount == 0) work.Release(); }
        private void DropQueued()
        {
            while (count > 0) { dropped += lengths[head]; head = (head + 1) % QueueCapacity; count--; }
        }
        private async Task RunAsync()
        {
            IEngineDiagnosticSink? sink = null;
            try {
                sink = open();
                stopping.Token.ThrowIfCancellationRequested();
                SetStage(EngineDiagnosticStage.Writing);
                await sink.WriteAsync($"\n[ Mana Table rules process {processId} ]\n".AsMemory(), stopping.Token).ConfigureAwait(false);
                while (true) {
                    stopping.Token.ThrowIfCancellationRequested();
                    int length; long omitted; bool finished;
                    lock (gate) {
                        length = count == 0 ? 0 : lengths[head];
                        if (length > 0) { queued[head].AsSpan(0, length).CopyTo(buffer); head = (head + 1) % QueueCapacity; count--; }
                        omitted = dropped - reportedDropped; reportedDropped = dropped;
                        finished = complete && count == 0 && length == 0;
                    }
                    if (omitted > 0) {
                        SetStage(EngineDiagnosticStage.Writing);
                        await sink.WriteAsync($"\n[ Mana Table diagnostics omitted {omitted} characters ]\n".AsMemory(), stopping.Token).ConfigureAwait(false);
                    }
                    if (length > 0) {
                        SetStage(EngineDiagnosticStage.Writing);
                        await sink.WriteAsync(buffer.AsMemory(0, length), stopping.Token).ConfigureAwait(false);
                        lock (gate) written += length;
                    }
                    bool empty;
                    lock (gate) { empty = count == 0; finished = complete && empty; }
                    if (empty) { SetStage(EngineDiagnosticStage.Flushing); await sink.FlushAsync(stopping.Token).ConfigureAwait(false); }
                    if (finished) break;
                    if (empty) await work.WaitAsync(stopping.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception) { lock (gate) { failure ??= stage; disabled = true; DropQueued(); } }
            finally {
                if (sink != null) {
                    SetStage(EngineDiagnosticStage.Disposing);
                    try { await sink.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { lock (gate) { cleanupFailed = true; failure ??= EngineDiagnosticStage.Disposing; } }
                }
                SetStage(EngineDiagnosticStage.Complete);
            }
        }
        private void SetStage(EngineDiagnosticStage value) { lock (gate) stage = value; }
    }
}
