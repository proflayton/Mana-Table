using System.Collections.Immutable;

namespace Mana.Table;

internal enum MixedGameStage
{
    HostObservation, GuestObservation, ManualBotStep, EvidenceDrain, AutomatedHistory,
    PublicComparison, HostStep, GuestStep, OwnerWait
}

// Scalar owner-loop evidence only. Successful polls and retired reads are not
// rules progress; no deadline, command, retry or qualification uses this ledger.
internal sealed class MixedGameProgress(Func<long> ticks, long frequency)
{
    private const int Capacity = 32;
    private readonly Dictionary<MixedGameStage, Totals> totals = [];
    private readonly Queue<MixedGameStageSpan> tail = [];
    private MixedGameStage? current;
    private long entered, attempt, sequence, superseded, lastTick = -1;
    private long? stopped;
    private int step;
    private MixedGamePublicProgress? publicProgress;

    internal void BeginAttempt() { RequireRunning(); attempt++; }

    internal void Enter(MixedGameStage stage, int steps)
    {
        RequireRunning();
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        long now = Now();
        Finish(now, "completed");
        current = stage; entered = now; step = steps;
    }

    internal void Complete() => Finish(Now(), "completed");

    internal void SupersededObservation()
    {
        if (current is not (MixedGameStage.HostObservation or MixedGameStage.GuestObservation))
            throw new InvalidOperationException("A superseded read must belong to its observation stage.");
        superseded++;
        Finish(Now(), "superseded");
    }

    internal void NewPublicBoard(long boardRevision)
        => publicProgress = new(Now(), boardRevision, attempt, step);

    internal void Stop(bool failed = false)
    {
        if (stopped != null) return;
        long now = Now();
        Finish(now, failed ? "failed" : "stopped");
        stopped = now;
    }

    internal MixedGameProgressEvidence Capture()
    {
        long now = stopped ?? Now();
        var active = current is { } stage
            ? new MixedGameStageSpan(sequence + 1, stage.ToString(), attempt, step, entered, now, "in-flight") : null;
        return new(frequency, attempt, sequence, Math.Max(0, sequence - Capacity), superseded,
            stopped, publicProgress, active, totals.OrderBy(pair => pair.Key).Select(pair => new MixedGameStageTotal(
                pair.Key.ToString(), pair.Value.Count, pair.Value.Superseded, pair.Value.Elapsed,
                pair.Value.Maximum, pair.Value.Last)).ToImmutableArray(), tail.ToImmutableArray());
    }

    private void Finish(long now, string outcome)
    {
        if (current is not { } stage) return;
        if (!totals.TryGetValue(stage, out var total)) totals.Add(stage, total = new());
        long duration = now - entered;
        total.Count++; total.Elapsed += duration; total.Maximum = Math.Max(total.Maximum, duration); total.Last = duration;
        if (outcome == "superseded") total.Superseded++;
        tail.Enqueue(new(++sequence, stage.ToString(), attempt, step, entered, now, outcome));
        if (tail.Count > Capacity) tail.Dequeue();
        current = null;
    }

    private long Now()
    {
        long now = ticks();
        if (frequency <= 0 || now < 0 || now < lastTick) throw new InvalidOperationException("Invalid progress clock.");
        lastTick = now;
        return now;
    }

    private void RequireRunning()
    {
        if (stopped != null) throw new InvalidOperationException("The active game progress ledger is stopped.");
    }

    private sealed class Totals
    {
        internal long Count, Superseded, Elapsed, Maximum, Last;
    }
}

internal sealed record MixedGameStageSpan(long Sequence, string Stage, long Attempt, int Steps,
    long EnteredElapsedTicks, long LeftElapsedTicks, string Outcome);
internal sealed record MixedGameStageTotal(string Stage, long Completed, long Superseded,
    long TotalElapsedTicks, long MaximumElapsedTicks, long LastElapsedTicks);
internal sealed record MixedGamePublicProgress(long ElapsedTicks, long BoardRevision, long Attempt, int Steps);
internal sealed record MixedGameProgressEvidence(long TimestampFrequency, long ObservationAttempts,
    long CompletedSpans, long DroppedSpans, long SupersededObservations, long? ActiveStopElapsedTicks,
    MixedGamePublicProgress? LastNewPublicBoard, MixedGameStageSpan? Current,
    ImmutableArray<MixedGameStageTotal> Stages, ImmutableArray<MixedGameStageSpan> Tail);
