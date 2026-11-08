namespace Mana.Table;

internal static class MixedGameProgressChecks
{
    internal static void Run(Action<bool, string> check)
    {
        long now = 0;
        var progress = new MixedGameProgress(() => now, 1000);
        progress.BeginAttempt(); progress.Enter(MixedGameStage.HostObservation, 0);
        now = 10; progress.Enter(MixedGameStage.GuestObservation, 0);
        now = 20; progress.SupersededObservation();
        var retired = progress.Capture();
        check(retired.ObservationAttempts == 1 && retired.SupersededObservations == 1
            && retired.Tail is [{ Stage: "HostObservation", Outcome: "completed", Steps: 0 },
                { Stage: "GuestObservation", Outcome: "superseded", Steps: 0 }],
            "An observation superseded before a counted game step retains its owner and attempt without inventing rules progress");
        progress.BeginAttempt(); progress.Enter(MixedGameStage.HostStep, 1);
        now = 16020; progress.Enter(MixedGameStage.PublicComparison, 1);
        progress.NewPublicBoard(762); progress.Complete();
        var before = progress.Capture();
        for (int i = 0; i < 80; i++) {
            now++; progress.BeginAttempt(); progress.Enter(MixedGameStage.HostObservation, 1);
            now++; progress.SupersededObservation();
        }
        var churn = progress.Capture();
        check(churn.Tail.Length == 32 && churn.DroppedSpans == churn.CompletedSpans - 32
            && churn.ObservationAttempts == 82 && churn.SupersededObservations == 81
            && churn.LastNewPublicBoard is { BoardRevision: 762, ElapsedTicks: 16020, Steps: 1 },
            "Repeated observation churn remains bounded and cannot refresh the last new public-board capture");
        check(churn.Stages.Single(stage => stage.Stage == "HostStep").MaximumElapsedTicks == 16000
            && churn.Tail.All(span => span.Stage != "HostStep") && before.Tail.Length == 4,
            "A long owner stage survives tail eviction in the aggregate while earlier immutable evidence stays unchanged");
        progress.Enter(MixedGameStage.OwnerWait, 1);
        now = 180000;
        var waiting = progress.Capture();
        check(waiting.Current is { Stage: "OwnerWait", LeftElapsedTicks: 180000 }
            && waiting.ActiveStopElapsedTicks == null && waiting.TimestampFrequency == 1000,
            "An active owner wait has its own clock interval and does not count as a completed input or game");
        progress.Stop();
        now = 180398;
        var stopped = progress.Capture();
        check(stopped.ActiveStopElapsedTicks == 180000 && stopped.Current == null
            && stopped.Tail.Last().LeftElapsedTicks == 180000 && waiting.Current != null,
            "Active-stop time is frozen before cleanup and cannot be extended by later evidence capture");
    }
}
