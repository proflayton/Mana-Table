using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

// Callbacks only retain immutable references and acceptance timestamps. Full
// duplicate comparison runs on a bounded consumer, never inside a receipt ACK.
internal sealed class FanoutAcceptedProjection
{
    internal const int PendingCapacity = 64;
    internal const int MaximumCheckpoints = 2 * ProjectionRecording.MaximumObservations;
    private readonly object gate = new();
    private readonly Channel<Entry> pending;
    private readonly Task consumer;
    private readonly List<GameSnapshot> observations = [];
    private readonly List<long> offsets = [];
    private readonly int maximumCheckpoints, pendingCapacity;
    private readonly Func<GameSnapshot, string> serialize;
    private long? origin, ended;
    private long accepted, identical, aliases, unretained, serializationCalls, serializationTicks, callbackTicks;
    // Includes an entry just dequeued until the consumer obtains gate for its
    // bookkeeping; highWater can therefore be channel capacity plus one.
    private int queued, highWater, retained;
    private string? failure;
    private GameSnapshot? lastEnqueued;
    private FanoutAcceptedProjectionWindow? completed;
    private readonly record struct Entry(GameSnapshot State, long At);
    internal int Count => Volatile.Read(ref retained);
    internal long AcceptedCallbacks => Interlocked.Read(ref accepted);
    internal long IdenticalCallbacks => Interlocked.Read(ref identical);
    internal string? Failure { get { lock (gate) return failure; } }

    internal FanoutAcceptedProjection(int pendingCapacity = PendingCapacity, int maximumCheckpoints = MaximumCheckpoints,
        Func<GameSnapshot, string>? serialize = null, Action? beforeDrain = null)
    {
        if (pendingCapacity is <= 0 or > PendingCapacity || maximumCheckpoints is <= 0 or > MaximumCheckpoints)
            throw new ArgumentOutOfRangeException(nameof(pendingCapacity));
        this.maximumCheckpoints = maximumCheckpoints; this.pendingCapacity = pendingCapacity;
        this.serialize = serialize ?? (state => JsonSerializer.Serialize(state));
        pending = Channel.CreateBounded<Entry>(new BoundedChannelOptions(pendingCapacity) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
        });
        consumer = Task.Run(() => ConsumeAsync(beforeDrain));
    }

    internal void Observe(GameSnapshot state)
    {
        long now = Stopwatch.GetTimestamp();
        lock (gate) {
            if (ended.HasValue) return; // Cleanup cannot mutate the frozen window.
            origin ??= now; accepted++;
            if (ReferenceEquals(lastEnqueued, state)) { aliases++; Interlocked.Increment(ref identical); }
            else if (failure != null) unretained++;
            else {
                queued++;
                if (!pending.Writer.TryWrite(new(state, now))) {
                    queued--; unretained++;
                    failure = "The bounded fanout pending-reference journal is full; evidence is incomplete.";
                } else { highWater = Math.Max(highWater, queued); lastEnqueued = state; }
            }
            callbackTicks += Stopwatch.GetTimestamp() - now;
        }
    }

    private async Task ConsumeAsync(Action? beforeDrain)
    {
        string? priorJson = null;
        try {
            beforeDrain?.Invoke();
            await foreach (var entry in pending.Reader.ReadAllAsync()) {
                lock (gate) queued--;
                var prior = observations.LastOrDefault();
                if (prior != null && SameIdentity(prior, entry.State)) {
                    priorJson ??= Serialize(prior);
                    string currentJson = Serialize(entry.State);
                    if (priorJson == currentJson) { Interlocked.Increment(ref identical); continue; }
                    priorJson = currentJson;
                } else priorJson = null;
                if (observations.Count >= maximumCheckpoints) {
                    lock (gate) { failure ??= "The bounded whole-fanout checkpoint quota is exhausted; evidence is incomplete."; unretained++; }
                    continue;
                }
                observations.Add(entry.State);
                offsets.Add(Stopwatch.GetElapsedTime(origin!.Value, entry.At).Ticks);
                Volatile.Write(ref retained, observations.Count);
            }
        } catch (Exception error) {
            lock (gate) failure ??= "Fanout evidence consumer failed: " + error.GetType().Name + ": " + error.Message;
            while (pending.Reader.TryRead(out _)) { lock (gate) { queued--; unretained++; } }
        }
    }

    private string Serialize(GameSnapshot state)
    {
        long start = Stopwatch.GetTimestamp();
        try { Interlocked.Increment(ref serializationCalls); return serialize(state); }
        finally { Interlocked.Add(ref serializationTicks, Stopwatch.GetTimestamp() - start); }
    }
    private static bool SameIdentity(GameSnapshot left, GameSnapshot right) => left.Id == right.Id && left.ViewerId == right.ViewerId
        && left.Revision == right.Revision && left.BoardRevision == right.BoardRevision && left.Decision?.Id == right.Decision?.Id;

    internal void ThrowIfFaulted()
    {
        if (Failure is { } error) throw new InvalidDataException(error);
    }
    internal void Freeze()
    {
        lock (gate) { if (ended.HasValue) return; ended = Stopwatch.GetTimestamp(); pending.Writer.TryComplete(); }
    }
    internal void Drain() { Freeze(); consumer.GetAwaiter().GetResult(); }
    internal FanoutCaptureDiagnostics Diagnostics => new(pendingCapacity, highWater, observations.Count, aliases,
        accepted - identical - observations.Count, serializationCalls, Stopwatch.GetElapsedTime(0, serializationTicks).TotalMilliseconds,
        Stopwatch.GetElapsedTime(0, callbackTicks).TotalMilliseconds, failure);
    internal FanoutAcceptedProjectionWindow Complete(bool requireComplete = true)
    {
        Drain();
        if (requireComplete) ThrowIfFaulted();
        if (origin == null || observations.Count == 0)
            throw new InvalidDataException("The fanout has no actual accepted playing observations to replay.");
        return completed ??= new(observations.ToImmutableArray(), offsets.ToImmutableArray(),
            Stopwatch.GetElapsedTime(origin.Value, ended!.Value).Ticks, accepted, identical,
            Diagnostics);
    }
}

internal sealed record FanoutCaptureDiagnostics(int PendingReferenceCapacity, int PendingReferenceHighWater, int RetainedReferences,
    long ReferenceIdenticalCallbacks, long UnretainedCallbacks, long PayloadSerializationCalls,
    double PayloadSerializationWallMs, double CallbackWallMs, string? Failure);
internal sealed record FanoutAcceptedProjectionWindow(ImmutableArray<GameSnapshot> Observations,
    ImmutableArray<long> AcceptedAtTicks, long EndTicks, long AcceptedCallbacks, long IdenticalCallbacks,
    FanoutCaptureDiagnostics? Capture = null);
