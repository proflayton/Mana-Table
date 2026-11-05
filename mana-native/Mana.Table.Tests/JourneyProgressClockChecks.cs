using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

internal static class JourneyProgressClockChecks
{
    internal static void Run(Action<bool, string> check)
    {
        StartBoundaries(check);
        FrozenCutoff(check);
        var time = new Clock(); var journey = New(time);
        time.Advance(44); journey.Check(); journey.BeginActiveMatch(); journey.BeginWork(110);
        time.Advance(14); journey.ObserveProgress(1);
        check(journey.SetupElapsed == TimeSpan.FromSeconds(44) && journey.ActiveMatchElapsed == TimeSpan.FromSeconds(14)
                && journey.WorkloadElapsed == TimeSpan.FromSeconds(14),
            "The monotonic journey gives setup and active match work separate fixed phase ownership");
        check(Throws<InvalidOperationException>(() => journey.BeginWork(110)) && Throws<InvalidOperationException>(journey.BeginActiveMatch),
            "A second active start or queue capture cannot reset either deadline");
        check(Throws<InvalidDataException>(() => journey.ObserveProgress(0)) && Throws<InvalidDataException>(() => journey.ObserveProgress(111)),
            "Regressed or out-of-capture semantic counts are rejected");
        time.Advance(14); journey.ObserveProgress(1); journey.Check(); time.Advance(1);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.ObserveProgress(110)),
            "Repeated polling or an unchanged receipt count cannot postpone a stall, and late terminal receipts cannot revive it");

        time = new(); journey = New(time); time.Advance(45);
        check(Throws<TimeoutException>(journey.BeginActiveMatch), "An active journey cannot start at or after the setup deadline");

        time = new(); journey = New(time);
        check(Throws<InvalidOperationException>(() => journey.BeginWork(110)) && Throws<InvalidOperationException>(() => journey.ObserveProgress(110)),
            "A queue or purported terminal count cannot bypass setup admission");

        time = new(); journey = New(time); journey.BeginActiveMatch(); journey.BeginWork(110);
        for (int count = 1; count <= 8; count++) { time.Advance(10); journey.ObserveProgress(count); }
        time.Advance(9); journey.ObserveProgress(109); time.Advance(1);
        check(Throws<TimeoutException>(() => journey.ObserveProgress(110)), "Healthy progress cannot extend the absolute active-journey deadline");

        time = new(); journey = New(time); journey.BeginActiveMatch(); journey.BeginWork(110); time.Advance(1); journey.ObserveProgress(110);
        time.Advance(89);
        check(Throws<TimeoutException>(journey.Complete), "Even all terminal receipts cannot qualify after the absolute deadline");

        time = new(); journey = New(time); journey.BeginActiveMatch(); journey.BeginWork(110); time.Advance(2); journey.ObserveProgress(55);
        time.Advance(3); journey.ObserveProgress(110); time.Utc = DateTimeOffset.MaxValue; journey.Complete(); time.Advance(200);
        check(journey.WorkloadElapsed == TimeSpan.FromSeconds(5) && journey.MaximumProgressGap == TimeSpan.FromSeconds(3),
            "Successful receipt timing freezes before teardown and ignores wall-clock changes");
        check(Throws<InvalidOperationException>(journey.Complete), "A completed journey cannot qualify a second time");
        time = new(); journey = New(time); journey.BeginActiveMatch(); journey.BeginWork(110); journey.ObserveProgress(110); journey.Stop(); time.Advance(91);
        check(Throws<InvalidOperationException>(journey.Complete), "Freezing failed evidence before teardown cannot qualify later using stopped time");

        // The actual failing CI cadence continued to90 receipts at45s. A fixed
        //45s workload rejects that healthy prefix;90s can complete it while the
        //independent15s watchdog still rejects a genuinely idle continuation.
        time = new(); var old = new JourneyProgressClock(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15), time);
        old.BeginActiveMatch(); old.BeginWork(110);
        for (int count = 1; count <= 8; count++) { time.Advance(5); old.ObserveProgress(count * 10); }
        time.Advance(5);
        check(Throws<TimeoutException>(() => old.ObserveProgress(90)), "The former45s cap rejects a steadily advancing90-of110 receipt prefix");
        time = new(); journey = New(time); journey.BeginActiveMatch(); journey.BeginWork(110);
        for (int count = 1; count <= 11; count++) { time.Advance(5); journey.ObserveProgress(count * 10); }
        journey.Complete();
        check(journey.Progress == 110 && journey.WorkloadElapsed == TimeSpan.FromSeconds(55),
            "The bounded90s workload admits the same continuing receipt cadence without extending on progress");

        // Synthetic provider+lobby intervals use retained396 stage durations.
        // Its actual Start request was not recorded: this models the revised
        // phase contract, never a retroactive qualification of that failure.
        time = new(); journey = New(time); time.Advance(24.5257896); journey.Check();
        time.Advance(6.1053235); journey.Check(); journey.BeginActiveMatch();
        time.Advance(13.0096946); journey.Check();
        time.Advance(1.5303572); journey.Check();
        check(journey.SetupElapsed == TimeSpan.FromSeconds(30.6311131)
                && journey.ActiveMatchElapsed == TimeSpan.FromSeconds(14.5400518) && !journey.InWorkload && journey.Progress == 0,
            "The revised synthetic phase admits setup only after both provider and lobby work, without inferring an unrecorded historical match-start time");
        check(Throws<InvalidOperationException>(() => journey.ObserveProgress(110)) && Throws<InvalidDataException>(journey.Complete),
            "A progressing preparation prefix cannot invent captured receipts or qualify as completed");

        time = new(); journey = New(time); time.Advance(44); journey.BeginActiveMatch();
        time.Advance(89); journey.Check(); journey.BeginWork(110);
        check(journey.ActiveMatchElapsed == TimeSpan.FromSeconds(89) && journey.WorkloadElapsed == TimeSpan.Zero,
            "Capturing a queue near the active deadline arms its watchdog without restarting the preparation clock");
        time.Advance(1);
        check(Throws<TimeoutException>(() => journey.ObserveProgress(110)) && Throws<TimeoutException>(journey.Check),
            "Delayed capture cannot acquire a fresh90 seconds or qualify on late terminal receipts");

        time = new(); journey = New(time); time.Advance(44); journey.BeginActiveMatch();
        time.Advance(89); journey.BeginWork(110); time.Advance(2);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.ObserveProgress(110)),
            "Even delayed setup admission and queue capture cannot escape the original135-second total envelope");

        time = new(); journey = New(time); time.Advance(44); journey.BeginActiveMatch(); time.Advance(20); journey.BeginWork(110);
        for (int count = 1; count <= 11; count++) { time.Advance(5); journey.ObserveProgress(count * 10); }
        journey.Complete();
        check(journey.SetupElapsed == TimeSpan.FromSeconds(44) && journey.ActiveMatchElapsed == TimeSpan.FromSeconds(75)
                && journey.WorkloadElapsed == TimeSpan.FromSeconds(55),
            "A controlled queue can finish after delayed initialization while sharing its unchanged90-second cap with preparation");

        time = new(); journey = New(time); journey.BeginActiveMatch();
        time.Advance(89); journey.Check(); time.Advance(1);
        check(Throws<TimeoutException>(journey.Check) && Throws<TimeoutException>(() => journey.BeginWork(110)),
            "Polling without a captured queue cannot extend the active preparation deadline");
    }

    private static void StartBoundaries(Action<bool, string> check)
    {
        var time = new Clock(); var journey = New(time); int starts = 0;
        time.Advance(44); journey.Check(); // Provider initialization completed.
        time.Advance(2); // Lobby preparation returned after the setup cutoff.
        check(Throws<TimeoutException>(() => PolledFanoutEngineJourney.StartMatchAsync(journey,
                () => { starts++; return Task.CompletedTask; }).GetAwaiter().GetResult()) && starts == 0 && !journey.InActiveMatch,
            "Provider setup at 44 s plus a late 2 s lobby result cannot invoke the actual match-start callback");

        time = new(); journey = New(time); time.Advance(45);
        check(Throws<TimeoutException>(() => PolledFanoutEngineJourney.StartMatchAsync(journey,
                () => { starts++; return Task.CompletedTask; }).GetAwaiter().GetResult()) && starts == 0,
            "Exactly 45 s rejects setup admission before any ordinary Start RPC");

        time = new(); journey = New(time); time.Advance(7);
        var ack = new TaskCompletionSource(); int policyCalls = 0;
        async Task StartThenPolicy()
        {
            await PolledFanoutEngineJourney.StartMatchAsync(journey, () => { starts++; return ack.Task; });
            policyCalls++;
        }
        var pending = StartThenPolicy(); time.Advance(89); journey.Check();
        check(starts == 1 && !pending.IsCompleted && policyCalls == 0
                && journey.SetupElapsed == TimeSpan.FromSeconds(7) && journey.ActiveMatchElapsed == TimeSpan.FromSeconds(89),
            "The real start wrapper begins active time before awaiting its held ACK and grants no policy continuation while held");
        time.Advance(1); ack.SetResult();
        check(pending.IsCompleted && Throws<TimeoutException>(() => pending.GetAwaiter().GetResult()) && policyCalls == 0,
            "A match-start ACK at the active deadline fails the post-ACK guard before policy or source work");

        time = new(); journey = New(time); time.Advance(44);
        PolledFanoutEngineJourney.StartMatchAsync(journey, () => Task.CompletedTask).GetAwaiter().GetResult();
        time.Advance(89); journey.BeginWork(110); time.Advance(1);
        check(Throws<TimeoutException>(() => journey.ObserveProgress(110)) && journey.Progress == 0
                && Throws<InvalidDataException>(journey.Complete)
                && journey.SetupElapsed == TimeSpan.FromSeconds(44) && journey.ActiveMatchElapsed == TimeSpan.FromSeconds(90),
            "Capturing at active 89 s cannot restart 90 s or admit a late receipt; completion still rejects its missing receipts");
        time.Advance(1);
        check(journey.TotalElapsed == TimeSpan.FromSeconds(135) && Throws<TimeoutException>(journey.Check),
            "The revised phase boundary retains the original absolute 135 s envelope including setup");
    }

    private static void FrozenCutoff(Action<bool, string> check)
    {
        var observed = Enumerable.Range(0, 109).Select(index => "execution-" + index).ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>(); var trace = new List<object>();
        var actions = new List<SourceAction> { new("old-read-action", "ability", "Read-only captured action") };
        var source = new SourceActionView(null, 0, SourceActionStatus.Idle, actions, "");
        var progress = new FiniteResolutionProgress(110, 109, 1, 0,
            ImmutableDictionary<ExecutionOutcome, int>.Empty.Add(ExecutionOutcome.Resolved, 109));
        var captured = PolledFanoutEngineJourney.CaptureStoppedPlayer(null, source, [], observed, progress, "active-deadline", errors, trace);
        string before = JsonSerializer.Serialize(captured);
        using var serialized = JsonDocument.Parse(before);
        // The same live owner can admit a final reply while finally pumps its
        // lanes. It must not mutate evidence already sampled before cleanup.
        observed.Add("execution-109"); errors.Add("late cleanup error"); trace.Add("late cleanup observation");
        actions.Add(new("later-read-action", "ability", "Later source read"));
        check(observed.Count == 110 && captured.Terminals.Length == 109
                && !captured.Terminals.Contains("execution-109") && JsonSerializer.Serialize(captured) == before
                && captured.Source.Actions.Count == 1
                && serialized.RootElement.GetProperty("progress").GetProperty("outcomes").GetProperty("Resolved").GetInt32() == 109,
            "The actual finally factory freezes serializable pre-cleanup 109-receipt progress and collections before a late 110th observation mutates the live owner");
        var later = PolledFanoutEngineJourney.CaptureStoppedPlayer(null, source, [], observed, progress, "later-observation", errors, trace);
        check(later.Terminals.Length == 110 && captured.Terminals.Length == 109 && later.Progress?.CompletedCount == 109,
            "A separate later sample can honestly observe 110 while credited progress stays 109, without clipping observations or mutating earlier pre-cleanup evidence");
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
