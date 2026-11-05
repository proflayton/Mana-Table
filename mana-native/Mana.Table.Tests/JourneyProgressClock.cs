namespace Mana.Table;

// Test journey deadlines use monotonic time. The caller owns the meaning and
// identity of its bounded semantic receipts; polling is never progress.
internal sealed class JourneyProgressClock
{
    internal const int PhaseContractVersion = 1;
    internal const string PhaseContractName = "setup45-active-match90-total135-stall15";
    private readonly TimeProvider time;
    private readonly TimeSpan setupBudget, activeMatchBudget, stallBudget;
    private readonly long started;
    private long? activeStarted, workStarted, stopped;
    private long lastProgress;
    private int progress, progressLimit;

    internal JourneyProgressClock(TimeSpan setupBudget, TimeSpan activeMatchBudget, TimeSpan stallBudget,
        TimeProvider? time = null)
    {
        if (setupBudget <= TimeSpan.Zero || activeMatchBudget <= TimeSpan.Zero || stallBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(setupBudget));
        this.time = time ?? TimeProvider.System;
        this.setupBudget = setupBudget; this.activeMatchBudget = activeMatchBudget; this.stallBudget = stallBudget;
        started = this.time.GetTimestamp();
    }

    internal bool InActiveMatch => activeStarted.HasValue;
    internal bool InWorkload => workStarted.HasValue;
    internal int Progress => progress;
    internal TimeSpan SetupElapsed => time.GetElapsedTime(started, activeStarted ?? Now);
    internal TimeSpan ActiveMatchElapsed => activeStarted is { } start ? time.GetElapsedTime(start, Now) : TimeSpan.Zero;
    internal TimeSpan TotalElapsed => time.GetElapsedTime(started, Now);
    internal long StartedTimestamp => started;
    internal long? StoppedTimestamp => stopped;
    internal long TimestampFrequency => time.TimestampFrequency;
    internal TimeSpan WorkloadElapsed => workStarted is { } start ? time.GetElapsedTime(start, Now) : TimeSpan.Zero;
    internal TimeSpan MaximumProgressGap { get; private set; }
    private long Now => stopped ?? time.GetTimestamp();

    internal void BeginActiveMatch()
    {
        if (activeStarted.HasValue || stopped.HasValue) throw new InvalidOperationException("A journey requests match start only once.");
        long now = Now; CheckAt(now);
        activeStarted = now;
    }

    internal void BeginWork(int expectedProgress)
    {
        if (!activeStarted.HasValue || workStarted.HasValue || stopped.HasValue)
            throw new InvalidOperationException("A live active match captures its workload only once.");
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
        if (time.GetElapsedTime(started, now) >= setupBudget + activeMatchBudget)
            throw new TimeoutException("The journey exceeded its fixed total envelope.");
        if (activeStarted is not { } active) {
            if (time.GetElapsedTime(started, now) >= setupBudget) throw new TimeoutException("The journey exceeded its fixed setup budget.");
            return;
        }
        if (time.GetElapsedTime(active, now) >= activeMatchBudget) throw new TimeoutException("The journey exceeded its fixed active-match budget.");
        // Initial preparation has no declared terminal set. Capturing it arms
        // this watchdog, while the active deadline retains its original owner.
        if (!workStarted.HasValue) return;
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
