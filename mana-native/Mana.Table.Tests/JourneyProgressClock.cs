namespace Mana.Table;

// Test journey deadlines use monotonic time. The caller owns the meaning and
// identity of its bounded semantic receipts; polling is never progress.
internal sealed class JourneyProgressClock
{
    private readonly TimeProvider time;
    private readonly TimeSpan setupBudget, workloadBudget, stallBudget;
    private readonly long started;
    private long? workStarted, stopped;
    private long lastProgress;
    private int progress, progressLimit;

    internal JourneyProgressClock(TimeSpan setupBudget, TimeSpan workloadBudget, TimeSpan stallBudget,
        TimeProvider? time = null)
    {
        if (setupBudget <= TimeSpan.Zero || workloadBudget <= TimeSpan.Zero || stallBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(setupBudget));
        this.time = time ?? TimeProvider.System;
        this.setupBudget = setupBudget; this.workloadBudget = workloadBudget; this.stallBudget = stallBudget;
        started = this.time.GetTimestamp();
    }

    internal bool InWorkload => workStarted.HasValue;
    internal int Progress => progress;
    internal TimeSpan SetupElapsed => time.GetElapsedTime(started, workStarted ?? Now);
    internal TimeSpan WorkloadElapsed => workStarted is { } start ? time.GetElapsedTime(start, Now) : TimeSpan.Zero;
    internal TimeSpan MaximumProgressGap { get; private set; }
    private long Now => stopped ?? time.GetTimestamp();

    internal void BeginWork(int expectedProgress)
    {
        if (workStarted.HasValue || stopped.HasValue) throw new InvalidOperationException("A journey starts its workload only once.");
        if (expectedProgress <= 0) throw new ArgumentOutOfRangeException(nameof(expectedProgress));
        long now = Now; CheckAt(now);
        workStarted = lastProgress = now; progressLimit = expectedProgress;
    }

    internal void ObserveProgress(int completed)
    {
        if (!workStarted.HasValue || stopped.HasValue) throw new InvalidOperationException("Progress requires a live workload.");
        long now = Now; CheckAt(now); // Late receipts cannot revive a stalled or expired journey.
        if (completed < progress || completed > progressLimit) throw new InvalidDataException("Journey semantic progress regressed or exceeded its captured bound.");
        if (completed == progress) return;
        var gap = time.GetElapsedTime(lastProgress, now);
        if (gap > MaximumProgressGap) MaximumProgressGap = gap;
        progress = completed; lastProgress = now;
    }

    internal void Check() => CheckAt(Now);

    private void CheckAt(long now)
    {
        if (workStarted is not { } start) {
            if (time.GetElapsedTime(started, now) >= setupBudget) throw new TimeoutException("The journey exceeded its fixed setup budget.");
            return;
        }
        if (time.GetElapsedTime(start, now) >= workloadBudget) throw new TimeoutException("The journey exceeded its fixed workload budget.");
        if (time.GetElapsedTime(lastProgress, now) >= stallBudget) throw new TimeoutException("The journey made no new captured terminal progress within its stall budget.");
    }

    internal void Complete()
    {
        if (stopped.HasValue) throw new InvalidOperationException("A stopped journey cannot qualify or complete again.");
        if (!workStarted.HasValue || progress != progressLimit) throw new InvalidDataException("A journey cannot complete without its captured semantic receipts.");
        long now = Now; CheckAt(now); stopped = now;
    }

    internal void Stop() => stopped ??= time.GetTimestamp();
}
