using System.Collections.Immutable;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

internal static class MatchLivenessRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var original = Ghost();
        var watch = MatchProgressWatch.Empty.Observe(MatchProgressWitness.From(original), TimeSpan.Zero);
        watch = watch.Observe(MatchProgressWitness.From(original with { Revision = 200, BoardRevision = 200 }), TimeSpan.FromSeconds(46));
        check(watch.Due(TimeSpan.FromSeconds(46)) && watch.Repair().Started == TimeSpan.Zero,
            "Successful polls, publication churn and reconciliation cannot renew a stalled rules-progress budget");
        check(!watch.Observe(MatchProgressWitness.From(original with { PhaseKey = "DRAW" }), TimeSpan.FromSeconds(46)).Due(TimeSpan.FromSeconds(46)),
            "Actual phase progress establishes a new liveness baseline");
        var selecting = original with { Decision = new("same-target-input", new(),
            new TargetSelectionPayload(new("Continue", true), new("Cancel", true), new(0, 100), []) { Progress = new(0, []) }) };
        var selectionWatch = MatchProgressWatch.Empty.Observe(MatchProgressWitness.From(selecting), TimeSpan.Zero);
        var selected = selecting with { Decision = selecting.Decision! with { Payload =
            ((TargetSelectionPayload)selecting.Decision.Payload) with { Progress = new(1, []) } } };
        check(!selectionWatch.Observe(MatchProgressWitness.From(selected), TimeSpan.FromSeconds(60)).Due(TimeSpan.FromSeconds(60)),
            "Actual staged target progress keeps a long compound input alive without relying on publication revisions");
        var lost = original with { MatchWaits = [new(0, 7, false), new(1, 9, true)] };
        var lostWatch = MatchProgressWatch.Empty.Observe(MatchProgressWitness.From(lost), TimeSpan.Zero);
        check(lostWatch.Observe(MatchProgressWitness.From(lost with { Turn = 12, PhaseKey = "DRAW" }), TimeSpan.FromSeconds(46)).Due(TimeSpan.FromSeconds(46)),
            "Another player's progress cannot conceal an unpublished wait owned by this seat");
        var reissued = Ready() with { MatchWaits = [new(0, 7, true)] };
        var reissueWatch = MatchProgressWatch.Empty.Observe(MatchProgressWitness.From(reissued), TimeSpan.Zero);
        check(reissueWatch.Observe(MatchProgressWitness.From(reissued with { Decision = reissued.Decision! with { Id = "reissued-scope" } }),
                TimeSpan.FromSeconds(46)).Due(TimeSpan.FromSeconds(46)),
            "Reissuing a publication scope for the same real input cannot manufacture rules progress");
        foreach (var readers in new[] { ImmutableArray.Create(0), ImmutableArray.Create(1, 1), ImmutableArray.Create(999) }) {
            bool refused = false;
            try { MatchProgressWitness.Validate(original with { MatchReadingWaits = readers }); } catch (IOException) { refused = true; }
            check(refused, "Foreign, duplicate and nonhuman reading owners cannot manufacture an unbounded AI pause: " + string.Join(',', readers));
        }
        await AiRepair(check, restored: true);
        await AiRepair(check, restored: false);
        await AiHumanWait(check);
        await AiUnownedObservationHold(check);
        await AiOwnedReadingHold(check);
        await AiPacingWait(check, human: true);
        await AiPacingWait(check, human: false);
        await AiSuperseded(check);
        await AiBlockedRepair(check);
        await AiMetadataChurn(check);
        await ClientRepair(check, restored: true);
        await ClientRepair(check, restored: false);
        await ClientBlockedRepair(check);
        await ClientHumanWait(check);
    }

    private static GameSnapshot Ghost(bool human = false) => new("liveness-game", "resolving", null, null, 0,
        0, "Pregame", "PREGAME", 0,
        [new(0, "Owner", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
         new(1, "Human", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null, null) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, ActionClass = "choice",
            HumanPlayerIds = human ? [0, 1] : [1]
        };
    private static GameSnapshot Ready(bool human = false) => Ghost(human) with { Status = "playing", Revision = 3, BoardRevision = 3,
        Decision = new("restored-input", new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true))) };

    private static async Task AiRepair(Action<bool, string> check, bool restored)
    {
        var clock = new Clock(); var port = new AiPort { Restore = restored }; var policy = new Policy();
        await using var host = new AiTableHost(port, _ => policy, 42, operationClock: clock);
        await host.StepAsync(); clock.Advance(44);
        port.State = port.State with { Revision = 500, BoardRevision = 500 }; await host.StepAsync();
        check(port.Repairs == 0 && port.Commands == 0, "AI liveness keeps its full bounded allowance before repair: " + restored);
        clock.Advance(1); await host.StepAsync();
        check(port.Repairs == 1 && port.Commands == 0 && policy.Calls == 0,
            "Recovering a missing AI decision never answers or replays it: " + restored);
        await host.StepAsync();
        if (restored) check(host.LastFault == null && port.Commands == 1 && policy.Calls == 1,
            "Only a fresh observation after repair permits the restored AI decision to execute once");
        else {
            await host.StepAsync();
            check(host.LastFault?.ReasonCode == "match-no-progress" && port.Reports == 1 && port.Repairs == 1 && port.Commands == 0,
                "An irreparable responsive engine yields one explicit AI fault instead of an endless resolving loop");
        }
    }

    private static async Task AiHumanWait(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new AiPort { State = Ghost() with { Status = "playing", DecisionOwnerId = 1 } };
        await using var host = new AiTableHost(port, _ => new Policy(), 42, operationClock: clock);
        await host.StepAsync(); clock.Advance(600); await host.StepAsync();
        check(port.Repairs == 0 && port.Reports == 0 && port.Commands == 0 && host.LastFault == null,
            "A human-owned required choice can wait indefinitely without an AI timeout, repair or invented answer");
    }

    private static async Task AiUnownedObservationHold(Action<bool, string> check)
    {
        var clock = new Clock();
        var port = new AiPort { State = Ghost() with { PriorityContinuation = new(1, "held-grant",
            PriorityContinuationMode.NoResponse, PriorityContinuationStatus.Held, "awaiting-observation", 0, []) } };
        await using var host = new AiTableHost(port, _ => new Policy(), 42, operationClock: clock);
        await host.StepAsync(); clock.Advance(46); await host.StepAsync();
        check(port.Repairs == 1, "A human's presence cannot exempt an unexplained AI observation hold from its progress budget");
    }

    private static async Task AiOwnedReadingHold(Action<bool, string> check)
    {
        var clock = new Clock();
        var port = new AiPort { State = Ghost() with { MatchReadingWaits = [1] } };
        await using var host = new AiTableHost(port, _ => new Policy(), 42, operationClock: clock);
        await host.StepAsync(); clock.Advance(600); await host.StepAsync();
        check(port.Repairs == 0 && host.LastFault == null, "Engine-reported exhausted human reading credit may wait without a false AI fault");
        port.State = port.State with { MatchReadingWaits = [] }; await host.StepAsync(); clock.Advance(46); await host.StepAsync();
        check(port.Repairs == 1, "Retiring actual human reading credit restores the automatic-work progress deadline");
    }

    private static async Task AiSuperseded(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new AiPort();
        await using var host = new AiTableHost(port, _ => new Policy(), 42, operationClock: clock);
        await host.StepAsync(); port.Superseded = true; await host.StepAsync(); clock.Advance(46);
        await host.StepAsync(); await host.StepAsync();
        check(host.LastFault?.ReasonCode == "observation-no-progress" && port.Reports == 1 && port.Commands == 0,
            "An endless sequence of superseded AI reads reaches a bounded explicit failure without replay");
    }

    private static async Task AiPacingWait(Action<bool, string> check, bool human)
    {
        var clock = new Clock(); var port = new AiPort();
        await using var host = new AiTableHost(port, _ => new Policy(), 42, pacing: new ClosedPacing(human), operationClock: clock);
        await host.StepAsync(); clock.Advance(60); await host.StepAsync(); await host.StepAsync();
        check(human ? port.Repairs == 0 && host.LastFault == null : port.Repairs == 1 && host.LastFault?.ReasonCode == "match-no-progress",
            human ? "An explicit human attention owner may suspend AI presentation without a false timeout"
                : "A closed presentation gate without a human owner cannot silently exempt automatic work from its deadline");
    }

    private sealed class ClosedPacing(bool human) : IAiHostPacing
    {
        public void Observe(ImmutableArray<AiTableProgress> progress) { }
        public bool Ready(string gameId, int seatId) => false;
        public bool WaitingForHuman(string gameId, int seatId) => human;
    }

    private static async Task AiMetadataChurn(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new AiPort { State = Ready(), StallExecution = true };
        await using var host = new AiTableHost(port, _ => new Policy(), 42, operationClock: clock);
        await host.StepAsync(); clock.Advance(20);
        port.State = port.State with { Revision = 20, BoardRevision = 20 }; await host.StepAsync();
        clock.Advance(26); port.State = port.State with { Revision = 40, BoardRevision = 40 };
        await host.StepAsync(); await host.StepAsync();
        check(host.LastFault != null && port.Reports == 1 && port.Repairs <= 1,
            "Fresh metadata cannot keep an accepted-but-unchanged automatic command cycle alive indefinitely");
    }

    private static async Task AiBlockedRepair(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new AiPort { Hold = true };
        await using var host = new AiTableHost(port, _ => new Policy(), 42, operationClock: clock);
        await host.StepAsync(); clock.Advance(45);
        var step = host.StepAsync(); await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(45);
        await step.WaitAsync(TimeSpan.FromSeconds(5)); port.Release.TrySetResult();
        await host.StepAsync();
        check(host.LastFault?.ReasonCode == "progress-recovery-failed" && port.Reports == 1 && port.Commands == 0,
            "AI recovery remains bounded even when its provider ignores cancellation; late work grants no action authority");
    }

    private static async Task ClientRepair(Action<bool, string> check, bool restored)
    {
        var clock = new Clock(); var port = new PlayerPort { Restore = restored };
        var client = new ClientSession(port, clock: clock); client.Accept(port.State); clock.Advance(45); client.Pump();
        await Until(client, () => !client.Busy && port.Repairs == 1);
        if (restored) check(!client.NeedsRecovery && client.State?.Decision?.Id == "restored-input" && port.Commands == 0,
            "The player session restores the current decision through one bounded repair without submitting a game command");
        else {
            client.Accept(port.State with { Revision = 1000, BoardRevision = 1000 }); client.Pump();
            check(client.NeedsRecovery && client.ProgressRecoveryRequired && client.State?.Id == "liveness-game"
                    && port.Repairs == 1 && port.Commands == 0,
                "Successful unchanged polls cannot erase a stalled-game recovery state or hide its retained board");
            client.Accept(Ready(human: true) with { Revision = 1001, BoardRevision = 1001 });
            check(!client.NeedsRecovery, "An independently restored human decision clears the stalled-game state");
        }
        client.Close();
    }

    private static async Task ClientBlockedRepair(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new PlayerPort { BlockSynchronously = true };
        var client = new ClientSession(port, clock: clock); client.Accept(port.State); clock.Advance(45); client.Pump();
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(45);
        await Until(client, () => !client.Busy);
        check(client.NeedsRecovery && client.State?.Id == "liveness-game" && port.Commands == 0,
            "A synchronously blocked recovery provider cannot hold the player session's operation lane forever");
        client.Reset(); client.Accept(Ready(human: true) with { Id = "replacement-game" });
        port.Release.Set(); await Task.Delay(20); client.Pump();
        check(client.State?.Id == "replacement-game" && !client.NeedsRecovery && port.Commands == 0,
            "A timed-out provider's late completion cannot overwrite a replacement game or execute a command");
        client.Close();
    }

    private static async Task ClientHumanWait(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new PlayerPort(); var client = new ClientSession(port, clock: clock);
        client.Accept(Ready(human: true)); clock.Advance(600); client.Pump();
        client.Accept(Ghost(human: true) with { Status = "playing", DecisionOwnerId = 1, Revision = 4, BoardRevision = 4 });
        clock.Advance(600); client.Pump();
        client.Accept(Ghost(human: true) with { Status = "playing", DecisionOwnerId = null, ActionClass = "simultaneous",
            MatchWaits = [new(1, 3, true)], Revision = 5, BoardRevision = 5 });
        clock.Advance(600); client.Pump();
        check(!client.NeedsRecovery && port.Repairs == 0 && port.Commands == 0,
            "Local, remote and simultaneous human choices are explicit unbounded waits rather than stalled automatic work");
        client.Close(); await Task.CompletedTask;
    }

    private static async Task Until(ClientSession session, Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) { session.Pump(); await Task.Delay(1, deadline.Token); }
        session.Pump();
    }

    private sealed class Policy : IAiPolicy
    {
        internal int Calls;
        public AiPolicyDescriptor Descriptor { get; } = new("liveness-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { Calls++; return Task.FromResult<AiProposal>(new AiProposal.Answer(new KeepOpeningHand(request.Frame.State.Id, request.Frame.State.Decision!.Id), "current")); }
    }
    private sealed class AiPort : IAutomatedSeatProvider, IAutomatedMatchProgressRecovery
    {
        internal GameSnapshot State = Ghost();
        internal bool Restore, Hold, Superseded, StallExecution;
        internal int Repairs, Commands, Reports;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsMatchProgressRecovery => true;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
            => Superseded ? Task.FromException<ImmutableArray<AutomatedSeatObservation>>(new EngineObservationSupersededException())
                : Task.FromResult(ImmutableArray.Create(new AutomatedSeatObservation(0, State, DecisionActionDomain.None, new())));
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        { Commands++; if (!StallExecution) State = State with { Status = "finished", Decision = null }; return Task.CompletedTask; }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        { Reports++; return Task.CompletedTask; }
        public async Task ReconcileAutomatedMatchProgressAsync(string gameId, int seatId, CancellationToken cancellation = default)
        { Repairs++; Started.TrySetResult(); if (Hold) await Release.Task; if (Restore) State = Ready() with { Revision = State.Revision + 1, BoardRevision = State.BoardRevision + 1 }; }
    }
    private sealed class PlayerPort : IMatchConnection, IMatchProgressRecovery
    {
        internal GameSnapshot State = Ghost(human: true);
        internal bool Restore, BlockSynchronously;
        internal int Repairs, Commands;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public bool SupportsMatchProgressRecovery => true;
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand command) { Commands++; return Task.CompletedTask; }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
        public Task ReconcileMatchProgressAsync(string gameId, CancellationToken cancellation = default)
        { Repairs++; Started.TrySetResult(); if (BlockSynchronously) Release.Wait(); if (Restore) State = Ready(human: true); return Task.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(int seconds) {
            Timer[] due;
            lock (gate) { ticks += TimeSpan.FromSeconds(seconds).Ticks; due = timers.Where(timer => timer.Due <= ticks).ToArray(); foreach (var timer in due) timer.Due = long.MaxValue; }
            foreach (var timer in due) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer(this, callback, state, dueTime);
        private sealed class Timer : ITimer
        {
            private readonly Clock owner; private readonly TimerCallback callback; private readonly object? state;
            internal long Due;
            internal Timer(Clock owner, TimerCallback callback, object? state, TimeSpan due) { this.owner = owner; this.callback = callback; this.state = state; lock (owner.gate) { owner.timers.Add(this); Change(due, Timeout.InfiniteTimeSpan); } }
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.gate) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks; return true; }
            public void Dispose() { lock (owner.gate) owner.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
