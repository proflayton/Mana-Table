using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Table;

internal static class AiCampaignStepWaitNegativeChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        var controlElapsed = Stopwatch.StartNew();
        AiCampaignContinuation.CheckRegression(check);
        var clock = new EngineDiagnosticsChecks.Clock();
        var wait = new AiCampaignStepWait("wait-bounds", clock);
        async Task Observe(GameSnapshot? state, bool active, string detail)
            => await Verify(wait, clock, state, active, detail, check);

        await Observe(State(100, 100), false, "The first independent public observation establishes its own timer baseline");
        await Observe(State(101, 101) with { Players = State(1, 1).Players.Take(3).ToImmutableArray() }, false,
            "An incomplete public roster cannot turn a larger board into active scheduling");
        await Observe(State(1000, 1000) with { Id = "foreign-game" }, false,
            "A foreign game never replaces this Match's public identity or scalar ceilings");
        await Observe(State(50, 102), false, "Rollback to B50 retains the earlier B100 scheduling ceiling");
        await Observe(State(51, 103), false, "B51 cannot manufacture advancement from the rolled-back prefix");
        await Observe(State(100, 104), false, "A complete B100 catch-up is a recovery baseline, not active work");
        await Observe(State(101, 105), true, "Only later strict B101 progress earns cooperative scheduling");
        await Observe(State(101, 106), false, "Metadata-only revision growth retains the existing idle timer");
        await Observe(State(102, 107) with { ViewerId = 0 }, false,
            "A private spectator impostor cannot supply driver scheduling evidence");
        await Observe(State(101, 108), false, "Recovery from invalid public input requires its own complete baseline");
        await Observe(State(102, 109), true, "A fresh complete public advance recovers normal active scheduling");

        foreach (var (invalid, detail) in new (GameSnapshot?, string)[] {
            (null, "absent public state"),
            (State(1000, -1), "negative public revision"),
            (State(-1, 1000), "negative public board"),
            (State(1000, 1000) with { Error = "provider error" }, "error state"),
            (State(1000, 1000) with { Status = "starting" }, "setup-only state"),
            (State(1000, 1000) with { Decision = new("foreign-input", new(), new PriorityPayload(new("Pass", true))) }, "disclosed spectator decision"),
            (State(1000, 1000) with { Players = [State(1, 1).Players[0], State(1, 1).Players[0], State(1, 1).Players[2], State(1, 1).Players[3]] }, "duplicate public identity"),
            (State(1000, 1000) with { Players = State(1, 1).Players.Select(player => player with { Id = player.Id + 10 }).ToImmutableArray() }, "replaced public roster"),
            (State(1000, 1000) with { Players = State(1, 1).Players.Select(player => player.Id > 0 ? player with { Eliminated = true } : player).ToImmutableArray() }, "nonterminal single survivor")
        }) await Observe(invalid, false, "The existing timer remains for " + detail + " without a new success grade");
        await Observe(State(102, 110), false, "Rejected invalid shapes do not raise or lower valid public ceilings");
        await Observe(State(103, 111), true, "Later original-roster progress can resume without adopting a foreign scope");

        var restored = State(104, 112) with { Undo = new(1, 1, null, new(1, "action", "execution", 0)) };
        await Observe(restored, false, "A new restoration epoch establishes a timer baseline even when public counters increase");
        await Observe(restored with { Revision = 113, BoardRevision = 105 }, true,
            "Strict later progress within the current restoration epoch can yield");
        await Observe(State(106, 114), false, "An older restoration epoch cannot lower the retained epoch or reopen progress");
        await Observe(restored with { Revision = 115, BoardRevision = 105 }, false,
            "Restoration rollback recovers only through a complete catch-up baseline");
        await Observe(restored with { Revision = 116, BoardRevision = 106 }, true,
            "Later current-epoch progress follows the recovered scalar ceilings");
        foreach (var undo in new UndoState[] {
            new(-1, 1, null, new(1, "action", "execution", 0)),
            new(1, -1, null, null), new(1, 0, null, new(1, "action", "execution", 0)),
            new(1, 2, null, null), new(1, 2, null, new(1, "action", "execution", 0))
        }) await Observe(State(1000, 1000) with { Undo = undo }, false,
            "Malformed restoration scalar versions remain idle and never replace the valid epoch");
        await Observe(restored with { Revision = 117, BoardRevision = 106 }, false,
            "Malformed restoration metadata leaves the prior valid high-water available for recovery");
        await Observe(restored with { Revision = 118, BoardRevision = 107 }, true,
            "A complete later public advance survives malformed restoration attempts without lowering its baseline");
        var restoredAgain = restored with { Undo = new(2, 2, null, new(2, "next-action", "next-execution", 0)) };
        await Observe(restoredAgain with { Revision = 119, BoardRevision = 108 }, false,
            "Restoration epoch two first establishes a new complete baseline");
        await Observe(restoredAgain with { Revision = 120, BoardRevision = 109 }, true,
            "Later epoch-two progress can yield without borrowing epoch-one scheduling state");
        await Observe(restored with { Revision = 121, BoardRevision = 110 }, false,
            "A positive epoch-one rollback cannot reopen progress after epoch two");
        await Observe(restoredAgain with { Revision = 122, BoardRevision = 109 }, false,
            "The current restoration epoch recovers at its retained high-water before yielding again");
        await Observe(restoredAgain with { Revision = 123, BoardRevision = 110 }, true,
            "Strict later epoch-two progress follows its recovery baseline");

        var otherClock = new EngineDiagnosticsChecks.Clock();
        var other = new AiCampaignStepWait("next-match", otherClock);
        await Verify(other, otherClock, State(1, 1) with { Id = "next-match" }, false,
            "A new Match establishes a new bounded baseline rather than borrowing prior progress", check);
        await Verify(other, otherClock, State(2, 2) with { Id = "next-match" }, true,
            "A separately bound Match can cooperate only after its own strict public advance", check);
        var boundedClock = new EngineDiagnosticsChecks.Clock();
        var bounded = new AiCampaignStepWait("wait-bounds", boundedClock);
        await Verify(bounded, boundedClock, State(1, 1), false, "A bounded sequence first pays its baseline timer", check);
        for (int next = 2; next <= 9; next++)
            await Verify(bounded, boundedClock, State(next, next), true, "Each independent strictly advanced public board funds one cooperative yield", check);
        for (int next = 10; next <= 17; next++)
            await Verify(bounded, boundedClock, State(9, next), false, "Repeated metadata-only publications cannot continue an active read burst", check);
        check(bounded.Evidence is { ActiveYields: 8, IdleTimers: 9, Completed: 17, Failed: 0 },
            "Eight actual public advancements fund exactly eight yields and repeated idle metadata always restores one timer per step");
        var summary = wait.Evidence;
        check(summary.Completed == summary.ActiveYields + summary.IdleTimers && summary.Failed == 0
                && summary.TimestampFrequency == Stopwatch.Frequency && summary.ActiveElapsedTicks >= 0 && summary.IdleElapsedTicks >= 0,
            "Constant-size scheduling totals count actual completed awaits without changing qualification or claiming CPU time");
        var primary = new IOException("Controlled timer failure");
        var broken = new AiCampaignStepWait("wait-bounds", new BrokenClock(primary));
        Exception? observed = null; try { await broken.WaitAsync(State(1, 1)); } catch (Exception error) { observed = error; }
        check(ReferenceEquals(observed, primary) && broken.Evidence is { Failed: 1, IdleTimers: 1, Completed: 0 },
            "Scheduling counters preserve the exact original wait failure instead of replacing its cause or recording a completed step");

        string directory = Path.Combine(profile, "manual-campaign-wait"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "scheduling-control-summary.json"), JsonSerializer.Serialize(new {
            scope = "Authored helper/host controls only; no natural game or historical saved-time claim", summary,
            nextMatch = other.Evidence, boundedSequence = bounded.Evidence, failedWait = broken.Evidence,
            totalControlledElapsedMilliseconds = controlElapsed.Elapsed.TotalMilliseconds,
            serializedSummaryBytes = JsonSerializer.SerializeToUtf8Bytes(summary).Length
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(JsonSerializer.SerializeToUtf8Bytes(summary).Length < 1024,
            "Scheduling evidence remains one small scalar result object, without a per-step trace or private snapshot");
    }

    private static async Task Verify(AiCampaignStepWait wait, EngineDiagnosticsChecks.Clock clock, GameSnapshot? state,
        bool active, string detail, Action<bool, string> check)
    {
        var before = wait.Evidence;
        var task = wait.WaitAsync(state);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!task.IsCompleted && clock.PendingTimers == 0) {
            guard.Token.ThrowIfCancellationRequested(); await Task.Yield();
        }
        bool timer = clock.PendingTimers != 0;
        if (timer) clock.Advance(TimeSpan.FromMilliseconds(1));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        check(timer != active && wait.Evidence.ActiveYields == before.ActiveYields + (active ? 1 : 0)
                && wait.Evidence.IdleTimers == before.IdleTimers + (active ? 0 : 1), detail);
    }

    private static GameSnapshot State(long board, long revision)
        => new("wait-bounds", "playing", null, null, -1, 1, "First main", "MAIN1", 0,
            Enumerable.Range(0, 4).Select(id => new Player(id, "Public seat", 40, true, false,
                ImmutableDictionary<string, int>.Empty, [])).ToImmutableArray(), [], null, null) {
                Revision = revision, BoardRevision = board
            };

    private sealed class BrokenClock(Exception error) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => throw error;
    }
}
