using System.Collections.Immutable;
using System.Diagnostics;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

// Test-composition evidence only. These values never authorize a policy or a
// command. All payloads are immutable provider/policy values, not callbacks
// which could retain a live collector or execute work on its owner.
internal abstract record CampaignEvidenceEvent
{
    internal sealed record Owned(AutomatedSeatObservation Observation) : CampaignEvidenceEvent;
    internal sealed record Batch(ImmutableArray<AutomatedSeatObservation> Observations, long ReturnSequence = 0) : CampaignEvidenceEvent;
    internal sealed record ValidateBatch(ImmutableArray<AutomatedSeatObservation> Observations) : CampaignEvidenceEvent;
    internal sealed record Public(GameSnapshot State) : CampaignEvidenceEvent;
    internal sealed record PolicyStarted(AiDecisionReplayCase Replay) : CampaignEvidenceEvent;
    internal sealed record Proposal(AiDecisionReplayCase Replay, AiDecisionFrame Frame, AiProposal Result) : CampaignEvidenceEvent;
    internal sealed record PolicyFailure(AiDecisionReplayCase Replay, string Error, string Stage) : CampaignEvidenceEvent;
    internal sealed record Receipt(AutomatedSeatObservation? Observation, GameCommand Command, bool Accepted, string? Error) : CampaignEvidenceEvent;
    internal sealed record StaleProbe(AiCampaignStaleProbe Result, DecisionCommand Command) : CampaignEvidenceEvent;
    internal sealed record HostFault(AiHostFault Fault, string? Error) : CampaignEvidenceEvent;
    internal sealed record StepCompleted(long ReturnedBatchSequence) : CampaignEvidenceEvent;
}

internal sealed record CampaignEvidenceStamp(long Sequence, DateTimeOffset CapturedAt, long MonotonicTicks, string Kind);
internal sealed record CampaignEvidenceHandoffStatus(int Capacity, int Pending, int HighWater, long Enqueued,
    long Applied, long ReturnedBatches, long ProcessedBatches, long ProcessedReturnSequence, long DiscardedAfterFault, bool Retired,
    string? Failure, ImmutableDictionary<int, int> AcceptedBySeat)
{
    public long TimestampFrequency { get; init; } = Stopwatch.Frequency;
    public string Bounds => "Pending event count, not a measured retained-byte limit; snapshots are private immutable values.";
}

internal sealed class AiCampaignEvidenceInbox(AiCampaignDiagnostics owner, int capacity = 256,
    TimeProvider? clock = null) : IAiHostDiagnostics
{
    private sealed record Entry(CampaignEvidenceStamp Stamp, CampaignEvidenceEvent Value);
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Queue<Entry> pending = [];
    private readonly Dictionary<int, int> accepted = [];
    private ImmutableArray<AutomatedSeatObservation> returned = [], processed = [];
    private long enqueued, applied, returnedBatches, processedBatches, processedReturnSequence, discarded;
    private int highWater, draining;
    private bool retired, faultRecorded;
    private Exception? fault;

    internal CampaignEvidenceHandoffStatus Status { get { lock (gate) return new(capacity, pending.Count, highWater,
        enqueued, applied, returnedBatches, processedBatches, processedReturnSequence, discarded, retired, fault?.Message, accepted.ToImmutableDictionary()) {
            TimestampFrequency = time.TimestampFrequency }; } }
    internal ImmutableArray<AutomatedSeatObservation> SnapshotObservations { get { lock (gate) return returned; } }
    internal ImmutableArray<AutomatedSeatObservation> ProcessedObservations { get { lock (gate) return processed; } }
    internal ImmutableDictionary<int, int> AcceptedBySeat { get { lock (gate) return accepted.ToImmutableDictionary(); } }

    internal void Post(CampaignEvidenceEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate) PostInState(value);
    }

    internal void EnsureActive()
    {
        lock (gate) {
            if (retired) throw new InvalidOperationException("The campaign evidence handoff has retired.");
            if (fault != null) throw fault;
        }
    }

    private void PostInState(CampaignEvidenceEvent value)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (retired) throw new InvalidOperationException("The campaign evidence handoff has retired.");
        if (fault != null) throw fault;
        if (pending.Count >= capacity) {
            fault = new InvalidDataException("The bounded campaign evidence handoff overflowed; qualification is incomplete.");
            throw fault;
        }
        var stamp = new CampaignEvidenceStamp(++enqueued, time.GetUtcNow(), time.GetTimestamp(), value.GetType().Name);
        pending.Enqueue(new(stamp, value)); highWater = Math.Max(highWater, pending.Count);
    }

    // Called only by the same serialized production AI worker on its next
    // Observe entry. A returned batch alone does not prove Step processed it.
    internal void BeginNextCycle()
    {
        lock (gate) {
            if (retired) throw new InvalidOperationException("The campaign evidence handoff has retired.");
            if (fault != null) throw fault;
            // A superseded/failed next read can enter again without returning a
            // new batch. A completion marker names that prior return only once.
            if (returnedBatches == processedReturnSequence) return;
            PostInState(new CampaignEvidenceEvent.StepCompleted(returnedBatches));
            processed = returned; processedBatches++; processedReturnSequence = returnedBatches;
        }
    }

    internal void Returned(ImmutableArray<AutomatedSeatObservation> observations)
    {
        if (observations.IsDefault) throw new InvalidDataException("A campaign batch must be an immutable initialized value.");
        lock (gate) {
            PostInState(new CampaignEvidenceEvent.Batch(observations, returnedBatches + 1));
            returned = observations; returnedBatches++;
        }
    }

    internal void Receipt(AutomatedSeatObservation? observation, GameCommand command, bool wasAccepted, string? error)
    {
        lock (gate) {
            PostInState(new CampaignEvidenceEvent.Receipt(observation, command, wasAccepted, error));
            if (wasAccepted && observation != null)
                accepted[observation.SeatId] = accepted.GetValueOrDefault(observation.SeatId) + 1;
        }
    }

    public void Report(AiHostFault hostFault, Exception? error)
        => Post(new CampaignEvidenceEvent.HostFault(hostFault, error == null ? null : ErrorText(error)));

    internal static string ErrorText(Exception error)
    {
        string text;
        try { text = error.ToString(); } catch { text = error.GetType().Name; }
        const int maximumCharacters = 65536;
        return text.Length <= maximumCharacters ? text : text[..maximumCharacters] + "\n[private error truncated]";
    }

    // The campaign's serial logical owner calls this across its asynchronous
    // continuations. No physical thread affinity is claimed. Producers only
    // enqueue; a second/reentrant drain cannot mutate the collector concurrently.
    internal void Drain()
    {
        if (Interlocked.CompareExchange(ref draining, 1, 0) != 0)
            throw new InvalidOperationException("The campaign evidence inbox already has a draining owner.");
        try {
            int count; lock (gate) count = pending.Count;
            while (count-- > 0) {
                Entry entry; lock (gate) entry = pending.Dequeue();
                try { owner.ApplyHandoff(entry.Stamp, entry.Value); }
                catch (Exception error) {
                    lock (gate) { fault ??= error; discarded += pending.Count; pending.Clear(); }
                    break;
                }
                lock (gate) applied++;
            }
            Exception? failure; bool record;
            lock (gate) { failure = fault; record = failure != null && !faultRecorded; if (record) faultRecorded = true; }
            if (record) owner.RecordHandoffFailure(failure!);
            if (failure != null) throw failure;
        } finally { Volatile.Write(ref draining, 0); }
    }

    internal void Retire()
    {
        lock (gate) {
            if (pending.Count != 0 || Volatile.Read(ref draining) != 0)
                throw new InvalidOperationException("Drain campaign evidence after stopping its producers before retirement.");
            retired = true;
        }
    }

    // The owner still closes its trace on a failing cleanup path. Abandonment
    // is counted and invalidates qualification; it is never a silent drain.
    internal Exception? RetireForDisposal()
    {
        lock (gate) {
            retired = true;
            if (pending.Count != 0 || Volatile.Read(ref draining) != 0) {
                fault ??= new InvalidDataException("The evidence owner disposed before its pending handoff was drained.");
                discarded += pending.Count; pending.Clear();
            }
            if (fault == null || faultRecorded) return null;
            faultRecorded = true;
            return fault;
        }
    }
}
