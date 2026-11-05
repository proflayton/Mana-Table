namespace Mana.Table;

internal static class JourneyProgressClockChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var time = new Clock(); var journey = New(time);
        time.Advance(44); journey.Check(); journey.BeginJourney(); journey.BeginWork(110);
        time.Advance(14); journey.ObserveProgress(1);
        check(journey.ReadinessElapsed == TimeSpan.FromSeconds(44) && journey.ActiveElapsed == TimeSpan.FromSeconds(14)
                && journey.WorkloadElapsed == TimeSpan.FromSeconds(14),
            "The monotonic journey gives provider readiness and active work separate fixed phase ownership");
        check(Throws<InvalidOperationException>(() => journey.BeginWork(110)) && Throws<InvalidOperationException>(journey.BeginJourney),
            "A second active start or queue capture cannot reset either deadline");
        check(Throws<InvalidDataException>(() => journey.ObserveProgress(0)) && Throws<InvalidDataException>(() => journey.ObserveProgress(111)),
            "Regressed or out-of-capture semantic counts are rejected");
        time.Advance(14); journey.ObserveProgress(1); journey.Check(); time.Advance(1);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.ObserveProgress(110)),
            "Repeated polling or an unchanged receipt count cannot postpone a stall, and late terminal receipts cannot revive it");

        time = new(); journey = New(time); time.Advance(45);
        check(Throws<TimeoutException>(journey.BeginJourney), "An active journey cannot start at or after the provider-readiness deadline");

        time = new(); journey = New(time);
        check(Throws<InvalidOperationException>(() => journey.BeginWork(110)) && Throws<InvalidOperationException>(() => journey.ObserveProgress(110)),
            "A queue or purported terminal count cannot bypass provider-readiness admission");

        time = new(); journey = New(time); journey.BeginJourney(); journey.BeginWork(110);
        for (int count = 1; count <= 8; count++) { time.Advance(10); journey.ObserveProgress(count); }
        time.Advance(9); journey.ObserveProgress(109); time.Advance(1);
        check(Throws<TimeoutException>(() => journey.ObserveProgress(110)), "Healthy progress cannot extend the absolute active-journey deadline");

        time = new(); journey = New(time); journey.BeginJourney(); journey.BeginWork(110); time.Advance(1); journey.ObserveProgress(110);
        time.Advance(89);
        check(Throws<TimeoutException>(journey.Complete), "Even all terminal receipts cannot qualify after the absolute deadline");

        time = new(); journey = New(time); journey.BeginJourney(); journey.BeginWork(110); time.Advance(2); journey.ObserveProgress(55);
        time.Advance(3); journey.ObserveProgress(110); time.Utc = DateTimeOffset.MaxValue; journey.Complete(); time.Advance(200);
        check(journey.WorkloadElapsed == TimeSpan.FromSeconds(5) && journey.MaximumProgressGap == TimeSpan.FromSeconds(3),
            "Successful receipt timing freezes before teardown and ignores wall-clock changes");
        check(Throws<InvalidOperationException>(journey.Complete), "A completed journey cannot qualify a second time");
        time = new(); journey = New(time); journey.BeginJourney(); journey.BeginWork(110); journey.ObserveProgress(110); journey.Stop(); time.Advance(91);
        check(Throws<InvalidOperationException>(journey.Complete), "Freezing failed evidence before teardown cannot qualify later using stopped time");

        // The actual failing CI cadence continued to90 receipts at45s. A fixed
        //45s workload rejects that healthy prefix;90s can complete it while the
        //independent15s watchdog still rejects a genuinely idle continuation.
        time = new(); var old = new JourneyProgressClock(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15), time);
        old.BeginJourney(); old.BeginWork(110);
        for (int count = 1; count <= 8; count++) { time.Advance(5); old.ObserveProgress(count * 10); }
        time.Advance(5);
        check(Throws<TimeoutException>(() => old.ObserveProgress(90)), "The former45s cap rejects a steadily advancing90-of110 receipt prefix");
        time = new(); journey = New(time); journey.BeginJourney(); journey.BeginWork(110);
        for (int count = 1; count <= 11; count++) { time.Advance(5); journey.ObserveProgress(count * 10); }
        journey.Complete();
        check(journey.Progress == 110 && journey.WorkloadElapsed == TimeSpan.FromSeconds(55),
            "The bounded90s workload admits the same continuing receipt cadence without extending on progress");

        // Master396's observed prefix: providers24.526s, table30.631s,
        // committed cast43.641s, then still resolving at45.171s. This is the
        // measured prefix only, not an invented successful engine continuation.
        time = new(); journey = New(time); time.Advance(24.5257896); journey.BeginJourney();
        time.Advance(6.1053235); journey.Check(); time.Advance(13.0096946); journey.Check();
        time.Advance(1.5303572); journey.Check();
        check(journey.ReadinessElapsed == TimeSpan.FromSeconds(24.5257896)
                && journey.ActiveElapsed == TimeSpan.FromSeconds(20.6453753) && !journey.InWorkload && journey.Progress == 0,
            "The measured396 initial-resolution prefix remains bounded active preparation after provider startup has completed");
        check(Throws<InvalidOperationException>(() => journey.ObserveProgress(110)) && Throws<InvalidDataException>(journey.Complete),
            "A progressing preparation prefix cannot invent captured receipts or qualify as completed");

        time = new(); journey = New(time); time.Advance(44); journey.BeginJourney();
        time.Advance(89); journey.Check(); journey.BeginWork(110);
        check(journey.ActiveElapsed == TimeSpan.FromSeconds(89) && journey.WorkloadElapsed == TimeSpan.Zero,
            "Capturing a queue near the active deadline arms its watchdog without restarting the preparation clock");
        time.Advance(1);
        check(Throws<TimeoutException>(() => journey.ObserveProgress(110)) && Throws<TimeoutException>(journey.Check),
            "Delayed capture cannot acquire a fresh90 seconds or qualify on late terminal receipts");

        time = new(); journey = New(time); time.Advance(44); journey.BeginJourney();
        time.Advance(89); journey.BeginWork(110); time.Advance(2);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.ObserveProgress(110)),
            "Even delayed provider admission and queue capture cannot escape the original135-second total envelope");

        time = new(); journey = New(time); time.Advance(44); journey.BeginJourney(); time.Advance(20); journey.BeginWork(110);
        for (int count = 1; count <= 11; count++) { time.Advance(5); journey.ObserveProgress(count * 10); }
        journey.Complete();
        check(journey.ReadinessElapsed == TimeSpan.FromSeconds(44) && journey.ActiveElapsed == TimeSpan.FromSeconds(75)
                && journey.WorkloadElapsed == TimeSpan.FromSeconds(55),
            "A controlled queue can finish after delayed initialization while sharing its unchanged90-second cap with preparation");

        time = new(); journey = New(time); journey.BeginJourney();
        time.Advance(89); journey.Check(); time.Advance(1);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.BeginWork(110)),
            "Polling without a captured queue cannot extend the active preparation deadline");
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
        internal void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
