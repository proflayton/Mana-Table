namespace Mana.Table;

internal static class JourneyProgressClockChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var time = new Clock(); var journey = New(time);
        time.Advance(44); journey.Check(); journey.BeginWork(110);
        time.Advance(14); journey.ObserveProgress(1);
        check(journey.SetupElapsed == TimeSpan.FromSeconds(44) && journey.WorkloadElapsed == TimeSpan.FromSeconds(14),
            "The monotonic journey gives setup and workload separate fixed phase ownership");
        check(Throws<InvalidOperationException>(() => journey.BeginWork(110)), "A second workload start cannot reset either deadline");
        check(Throws<InvalidDataException>(() => journey.ObserveProgress(0)) && Throws<InvalidDataException>(() => journey.ObserveProgress(111)),
            "Regressed or out-of-capture semantic counts are rejected");
        time.Advance(14); journey.ObserveProgress(1); journey.Check(); time.Advance(1);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.ObserveProgress(110)),
            "Repeated polling or an unchanged receipt count cannot postpone a stall, and late terminal receipts cannot revive it");

        time = new(); journey = New(time); time.Advance(45);
        check(Throws<TimeoutException>(() => journey.BeginWork(110)), "A workload cannot start at or after the setup deadline");

        time = new(); journey = New(time); journey.BeginWork(110);
        for (int count = 1; count <= 8; count++) { time.Advance(10); journey.ObserveProgress(count); }
        time.Advance(9); journey.ObserveProgress(109); time.Advance(1);
        check(Throws<TimeoutException>(() => journey.ObserveProgress(110)), "Healthy progress cannot extend the absolute workload deadline");

        time = new(); journey = New(time); journey.BeginWork(110); time.Advance(1); journey.ObserveProgress(110);
        time.Advance(89);
        check(Throws<TimeoutException>(journey.Complete), "Even all terminal receipts cannot qualify after the absolute deadline");

        time = new(); journey = New(time); journey.BeginWork(110); time.Advance(2); journey.ObserveProgress(55);
        time.Advance(3); journey.ObserveProgress(110); time.Utc = DateTimeOffset.MaxValue; journey.Complete(); time.Advance(200);
        check(journey.WorkloadElapsed == TimeSpan.FromSeconds(5) && journey.MaximumProgressGap == TimeSpan.FromSeconds(3),
            "Successful receipt timing freezes before teardown and ignores wall-clock changes");
        check(Throws<InvalidOperationException>(journey.Complete), "A completed journey cannot qualify a second time");
        time = new(); journey = New(time); journey.BeginWork(110); journey.ObserveProgress(110); journey.Stop(); time.Advance(91);
        check(Throws<InvalidOperationException>(journey.Complete), "Freezing failed evidence before teardown cannot qualify later using stopped time");

        // The actual failing CI cadence continued to90 receipts at45s. A fixed
        //45s workload rejects that healthy prefix;90s can complete it while the
        //independent15s watchdog still rejects a genuinely idle continuation.
        time = new(); var old = new JourneyProgressClock(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15), time);
        old.BeginWork(110);
        for (int count = 1; count <= 8; count++) { time.Advance(5); old.ObserveProgress(count * 10); }
        time.Advance(5);
        check(Throws<TimeoutException>(() => old.ObserveProgress(90)), "The former45s cap rejects a steadily advancing90-of110 receipt prefix");
        time = new(); journey = New(time); journey.BeginWork(110);
        for (int count = 1; count <= 11; count++) { time.Advance(5); journey.ObserveProgress(count * 10); }
        journey.Complete();
        check(journey.Progress == 110 && journey.WorkloadElapsed == TimeSpan.FromSeconds(55),
            "The bounded90s workload admits the same continuing receipt cadence without extending on progress");
    }

    private static JourneyProgressClock New(TimeProvider time) => new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(15), time);
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        internal DateTimeOffset Utc = DateTimeOffset.UnixEpoch;
        internal void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
