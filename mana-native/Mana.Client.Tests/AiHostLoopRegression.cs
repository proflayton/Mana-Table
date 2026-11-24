using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

// Actual background host and provider boundaries. Authored immutable receipts
// model admission/observation order, not game resolution or policy fulfillment.
internal static partial class AiHostLoopRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ControlledMixedPublicRead(check);
        await StaticHumanModal(check);
        await MixedOwnerRecovery(check);
        await MixedOwnerRecovery(check, inconsistent: true);
        await MixedOwnershipShapes(check);
        await MixedReadWorkBound(check);
        await AcceptedRefresh(check);
        await NewAiOnlyPublicBoard(check);
        await NewBoardFindsCurrentDecision(check);
        await MixedTableBecomesAiOnly(check);
        await PacingClosesDuringBoardStep(check);
        await PacingClosesDuringBoardStep(check, mixed: true);
        foreach (string mode in new[] { "unchanged", "metadata", "omitted-roster", "inconsistent-roster",
            "new-game", "missing-seat", "single-living", "rollback", "finished", "empty", "pacing", "mixed-pacing", "fault", "rejected" })
            await NoPublicBoardHint(mode, check);
        foreach (string mode in new[] { "empty", "resolving", "superseded", "rejected", "unknown", "policy-failure" })
            await IdleOutcome(mode, check);
        foreach (string mode in new[] { "mixed-rejected", "mixed-unknown" }) await MixedOutcome(mode, check);
        await RealPacing(check);
        await AcceptedWatchdog(check);
        await ForeignHumanWait(check);
        await DirectStep(check);
    }

    private static async Task AcceptedRefresh(Action<bool, string> check)
    {
        var clock = new Clock(); var original = Frame(); var fresh = Frame("next-current", 3);
        var provider = new Port(clock) { Reply = read => read switch {
            1 => [original], 2 => [Resolving(original, 1)], 3 => [original], _ => [fresh]
        }, HoldRead = 5 };
        var policy = new Policy(); var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        try {
            await host.StartAsync();
            // The actual idle timer is the completed-loop barrier for both the
            // old unconditional-delay baseline and the new resolving reply.
            await Until(() => clock.IdlePending > 0);
            Console.WriteLine("AI loop first accepted boundary: reads=" + provider.Reads
                + "; idleTimers=" + clock.IdlePending + "; readStarts=" + string.Join(',', provider.ReadStarts));
            check(provider.Reads == 2 && provider.ReadStarts.SequenceEqual([0L, 0L]),
                "An accepted AI submission immediately requests one fresh observation without a fixed idle delay");
            await Until(() => clock.IdlePending == 1);
            check(provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "accepted-current" }]
                    && policy.Calls == 1 && Accepted(host) == 1,
                "The immediate resolving observation does not establish input consumption or grant replay");
            clock.Advance(39);
            check(provider.Reads == 2, "Resolving work keeps the unchanged 40 ms idle delay through 39 ms");
            clock.Advance(1); await Until(() => provider.Reads >= 3 && clock.IdlePending == 1);
            check(provider.Commands.Count == 1 && policy.Calls == 1 && provider.ReadStarts.SequenceEqual([0L, 0L, 40L]),
                "The identical old stamp after a same-revision resolving frame cannot replay its accepted command or request another immediate read");
            clock.Advance(40); await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "accepted-current" }, KeepOpeningHand { DecisionId: "next-current" }]
                    && provider.ReadStarts.SequenceEqual([0L, 0L, 40L, 80L, 80L]) && policy.Calls == 2
                    && Accepted(host) == 2 && host.LastFault == null,
                "Only independently observed fresh authority submits once and receives its own immediate post-ACK observation");
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            provider.Release.TrySetResult();
            check(provider.Commands.Count == 2 && provider.Reads == 5 && clock.Pending == 0 && provider.Reports.IsEmpty,
                "Disposal cancels the held immediate observation, joins the host and leaves no timer, late policy or replay");
        } finally { provider.Release.TrySetResult(); await host.DisposeAsync(); }
    }

    private static async Task NewAiOnlyPublicBoard(Action<bool, string> check)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Reply = read => read switch {
            1 => BoardBatch(1, 1), 2 => BoardBatch(2, 2), _ => BoardBatch(2, 3)
        } };
        var policy = new Policy();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        check(provider.Reads == 1 && provider.Commands.IsEmpty && policy.Calls == 0,
            "The first AI-only resolving publication establishes a baseline without a speculative read or command");
        clock.Advance(40);
        await Until(() => clock.IdlePending == 1);
        Console.WriteLine("AI-only public-board witness: reads=" + provider.Reads
            + "; readStarts=" + string.Join(',', provider.ReadStarts) + "; commands=" + provider.Commands.Count
            + "; policyCalls=" + policy.Calls + "; idleTimers=" + clock.IdlePending);
        check(provider.Reads == 3 && provider.ReadStarts.SequenceEqual([0L, 40L, 40L])
                && provider.Commands.IsEmpty && policy.Calls == 0 && host.LastFault == null,
            "A genuinely advanced AI-only public board requests one fresh observation before treating the continuing rules transition as idle");
        clock.Advance(39);
        check(provider.Reads == 3 && provider.Commands.IsEmpty,
            "An unchanged board with only a newer metadata revision retains the full ordinary 40 ms idle interval");
    }

    private static async Task NewBoardFindsCurrentDecision(Action<bool, string> check)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Reply = read => read switch {
            1 => BoardBatch(1, 1), 2 => BoardBatch(2, 2), _ => BoardBatch(2, 3, ownPrompt: true)
        }, HoldRead = 4 };
        var policy = new Policy();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        try {
            await host.StartAsync(); await Until(() => clock.IdlePending == 1);
            clock.Advance(40); await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(provider.ReadStarts.SequenceEqual([0L, 40L, 40L, 40L])
                    && provider.Commands.ToArray() is [KeepOpeningHand { DecisionId: "public-progress-current" }]
                    && policy.Calls == 1 && Accepted(host) == 1 && host.LastFault == null,
                "An AI-only public-board hint obtains a separate exact owned prompt before one normal policy command and its accepted refresh");
        } finally { provider.Release.TrySetResult(); }
    }

    private static async Task MixedTableBecomesAiOnly(Action<bool, string> check)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Reply = read => read switch {
            1 => BoardBatch(1, 1, humans: 2),
            2 => BoardBatch(2, 2, humans: 2, eliminatedHumans: true),
            _ => BoardBatch(2, 3, humans: 2, eliminatedHumans: true)
        } };
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        clock.Advance(40); await Until(() => clock.IdlePending == 1);
        check(provider.Reads == 3 && provider.ReadStarts.SequenceEqual([0L, 40L, 40L]) && provider.Commands.IsEmpty,
            "Two actual remaining AI owners can request a fresh board after both humans become eliminated while all four original player IDs remain bound");
    }

    private static async Task NoPublicBoardHint(string mode, Action<bool, string> check)
    {
        var clock = new Clock(); var policy = new Policy(mode is "pacing" or "mixed-pacing" ? AiPriorityAutomation.NoResponse : AiPriorityAutomation.FullControl) { Fail = mode == "fault" };
        int humans = mode is "human" or "omitted-roster" or "mixed-pacing" ? 2 : 0;
        bool paced = mode is "pacing" or "mixed-pacing";
        long initialBoard = mode == "rollback" ? 2 : 1;
        var initial = BoardBatch(initialBoard, initialBoard, humans: humans);
        var next = BoardBatch(mode is "unchanged" or "metadata" ? 1 : 3,
            mode == "unchanged" ? 1 : 3, humans: humans, ownPrompt: mode is "fault" or "rejected");
        next = mode switch {
            "omitted-roster" => next.Select(row => row with { State = row.State with {
                Players = row.State.Players.Where(player => player.Id >= 2).ToImmutableArray() } }).ToImmutableArray(),
            "inconsistent-roster" => next.SetItem(1, next[1] with { State = next[1].State with {
                Players = next[1].State.Players.Add(Frame(seat: 2).State.Viewer!) } }),
            "new-game" => next.Select(row => row with { State = row.State with { Id = "another-public-game" } }).ToImmutableArray(),
            "missing-seat" => [next[0]],
            "single-living" => next.Select(row => row with { State = row.State with {
                Players = row.State.Players.Select(player => player.Id == 1 ? player with { Eliminated = true } : player).ToImmutableArray() } }).ToImmutableArray(),
            "rollback" => next.SetItem(1, next[1] with { State = next[1].State with { Revision = 1, BoardRevision = 1 } }),
            "finished" => next.Select(row => row with { State = row.State with { Status = "finished",
                Outcome = new(row.State.Id, MatchOutcomeKind.Draw, [], [], "AllPlayersDrew") } }).ToImmutableArray(),
            "empty" => [],
            _ => next
        };
        if (paced) {
            ImmutableArray<AutomatedSeatObservation> Announced(ImmutableArray<AutomatedSeatObservation> rows) => rows.Select(row => row with {
                State = row.State with {
                    Stack = [new("New public spell", "", false, null) { Id = "paced-public-spell" }],
                    Decision = row.SeatId == humans ? new("paced-priority", new(), new PriorityPayload(new("Pass", true), CanAutoPass: true) {
                        ResponseAvailability = PriorityResponseAvailability.Absent }) : null,
                    DecisionOwnerId = humans, PriorityPlayerId = humans, StackInterruptionVersion = 0,
                    PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                    PriorityObservation = new(row.State.BoardRevision, 0, 0, 0, true, false)
                }
            }).ToImmutableArray();
            initial = Announced(initial); next = Announced(next);
        }
        var provider = new Port(clock) { Reply = read => read == 1 ? initial
                : read >= 3 && (mode is "empty" or "missing-seat" or "finished" or "new-game") ? BoardBatch(9, 9) : next,
            Mode = mode == "rejected" ? "rejected" : "", SupportsPriority = paced };
        var pacing = paced ? new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock) : null;
        await using var host = new AiTableHost(provider, _ => policy, 42, pacing: pacing, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        clock.Advance(40); await Until(() => clock.IdlePending == 1);
        check(provider.Reads == 2 && provider.ReadStarts.SequenceEqual([0L, 40L])
                && provider.Commands.Count == (mode == "rejected" ? 1 : 0)
                && policy.Calls == (mode is "fault" or "rejected" ? 1 : 0),
            "A public-board hint cannot bypass ordinary idle, ownership, complete roster, monotonic scope, pacing or failure handling: " + mode);
        clock.Advance(39);
        check(provider.Reads == 2,
            "The full 40 ms idle interval remains through39ms after the noneligible board: " + mode);
        if (paced) {
            clock.Advance(2320); await Until(() => clock.IdlePending == 1);
            check(provider.Controls.IsEmpty && provider.Commands.IsEmpty && policy.Calls == 0,
                "Both live AI rows retain the real 2.4 second announcement barrier through2399ms without an ACK, grant, command or evaluation");
            clock.Advance(40); await Until(() => clock.IdlePending == 1);
            check(provider.Controls.ToArray() is [AutomatedPriorityControl.Acknowledge]
                    && provider.Commands.IsEmpty && policy.Calls == 0 && host.LastFault == null,
                "Only an independently observed post-beat priority frame attempts its scoped ACK; its known-retired control cannot become consent or a policy pass");
        }
        if (mode is "empty" or "missing-seat" or "finished" or "new-game") {
            clock.Advance(1); await Until(() => clock.IdlePending == 1);
            check(provider.Reads == 3 && provider.ReadStarts.SequenceEqual([0L, 40L, 80L]) && provider.Commands.IsEmpty,
                "Reappearing owned scopes establish a fresh baseline instead of reviving retired public-board high-water: " + mode);
        }
    }

    private static async Task PacingClosesDuringBoardStep(Action<bool, string> check, bool mixed = false)
    {
        var clock = new Clock(); var pace = new ClosingPace(); var policy = new Policy();
        var provider = new Port(clock) { Reply = read => read == 1 ? BoardBatch(1, 1, humans: mixed ? 2 : 0)
            : BoardBatch(2, 2, humans: mixed ? 2 : 0, ownPrompt: true) };
        await using var host = new AiTableHost(provider, _ => policy, 42, pacing: pace, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        clock.Advance(40); await Until(() => clock.IdlePending == 1);
        Console.WriteLine("AI public-board final pacing witness: reads=" + provider.Reads + "; closed=" + pace.Closed
            + "; commands=" + provider.Commands.Count + "; policyCalls=" + policy.Calls);
        check(pace.Closed && provider.Reads == 2 && provider.Commands.IsEmpty && policy.Calls == 0,
            "A pacing barrier closing during the current board step suppresses its read hint as well as policy and commands");
    }

    private static ImmutableArray<AutomatedSeatObservation> BoardBatch(long board, long revision,
        int humans = 0, bool eliminatedHumans = false, bool ownPrompt = false)
    {
        var players = Enumerable.Range(0, humans + 2).Select(id => Frame(seat: id).State.Viewer! with {
            Eliminated = id < humans && eliminatedHumans }).ToImmutableArray();
        return Enumerable.Range(humans, 2).Select(seat => {
            var source = Frame("public-progress-current", revision, seat);
            return source with { State = source.State with { Players = players, BoardRevision = board,
                Turn = 1, ActivePlayerId = humans, DecisionOwnerId = ownPrompt ? humans : null,
                Decision = ownPrompt && seat == humans ? source.State.Decision : null,
                ActionClass = ownPrompt ? "decision" : "resolving"
            } };
        }).ToImmutableArray();
    }

    private sealed class ClosingPace : IAiHostPacing
    {
        private int observations, readyCalls;
        internal bool Closed { get; private set; }
        public void Observe(ImmutableArray<AiTableProgress> progress) => observations++;
        public bool Ready(string gameId, int seatId) {
            if (observations < 2) return true;
            if (++readyCalls <= 2) return true;
            Closed = true; return false;
        }
    }

    private static async Task IdleOutcome(string mode, Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Frame();
        var provider = new Port(clock) { Mode = mode, Reply = _ => mode switch {
            "empty" => [], "resolving" => [Resolving(initial, 1)], _ => [initial]
        } };
        var policy = new Policy { Fail = mode == "policy-failure" };
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending > 0 || provider.Reads > 1);
        int expectedCommands = mode is "rejected" or "unknown" ? 1 : 0;
        check(clock.IdlePending == 1 && provider.Reads == 1 && provider.Commands.Count == expectedCommands
                && Accepted(host) == 0 && (mode is "unknown" or "policy-failure" ? host.LastFault != null : host.LastFault == null),
            "No accepted submission means ordinary idle/backoff rather than an immediate observation: " + mode);
        clock.Advance(39);
        check(provider.Reads == 1 && provider.Commands.Count == expectedCommands,
            "An idle or unsafe outcome cannot poll early or replay a command before 40 ms: " + mode);
    }

    private static async Task RealPacing(Action<bool, string> check)
    {
        var clock = new Clock(); var announced = Frame() with { State = Frame().State with {
            Stack = [new("Public announcement", "", false, null) { Id = "announced-spell" }]
        } };
        var provider = new Port(clock) { Reply = _ => [announced], HoldRead = 4 };
        var policy = new Policy(); var pacing = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
        await using var host = new AiTableHost(provider, _ => policy, 42, pacing: pacing, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        check(provider.Reads == 1 && provider.Commands.IsEmpty && policy.Calls == 0,
            "The real shipping announcement pacer withholds policy and commands while the AI loop remains on idle cadence");
        clock.Advance(2399); await Until(() => provider.Reads >= 2 && clock.IdlePending == 1);
        check(provider.Commands.IsEmpty && policy.Calls == 0,
            "A fresh observation before the real 2.4 s beat cannot bypass presentation pacing");
        clock.Advance(40); await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(provider.Commands.Count == 1 && policy.Calls == 1 && provider.Reads == 4
                && provider.ReadStarts.ToArray() is [0, 2399, 2439, 2439] && Accepted(host) == 1,
            "After the shipping beat a fresh current command submits once and only its ACK earns an immediate read");
        provider.Release.TrySetResult();
    }

    private static async Task MixedOutcome(string mode, Action<bool, string> check)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Mode = mode, Reply = _ => [Frame(), Frame("other-seat", seat: 1)] };
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending > 0 || provider.Reads > 1);
        check(provider.Reads == 1 && clock.IdlePending == 1 && provider.Commands.Count == 2 && Accepted(host) == 1,
            "A mixed batch containing an accepted command and an unsafe outcome retains idle backoff: " + mode);
        clock.Advance(39);
        check(provider.Reads == 1 && provider.Commands.Count == 2,
            "An acceptance cannot override another seat's rejected or uncertain receipt and spin the host: " + mode);
    }

    private static async Task AcceptedWatchdog(Action<bool, string> check)
    {
        var clock = new Clock(); var provider = new Port(clock); var policy = new Policy();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StartAsync(); await Until(() => provider.Reads >= 2 && clock.IdlePending == 1);
        clock.Advance(44000); await Until(() => provider.Reads >= 3 && clock.IdlePending == 1);
        check(host.LastFault == null && provider.Commands.Count == 1 && Accepted(host) == 1,
            "The immediate ACK refresh and later identical reads do not renew or prematurely expire the existing accepted-input watchdog");
        clock.Advance(1000); await Until(() => provider.Reports.Count == 1 && clock.IdlePending == 1);
        check(host.LastFault?.ReasonCode == "accepted-no-progress" && provider.Commands.Count == 1 && policy.Calls == 1
                && Accepted(host) == 1 && host.Histories.All(history => history.Decisions.All(row => row.Outcome != AiDecisionOutcome.ExecutionUnknown)),
            "The unchanged 45 s accepted-input deadline faults once without replay or laundering the Accepted receipt");
        clock.Advance(40); await Until(() => provider.Reads >= 5 && clock.IdlePending == 1);
        check(provider.Reports.Count == 1 && provider.Commands.Count == 1,
            "A faulted table keeps idle backoff and cannot emit another report or command on its next observation");
    }

    private static async Task DirectStep(Action<bool, string> check)
    {
        var clock = new Clock(); var provider = new Port(clock); var policy = new Policy();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StepAsync();
        check(provider.Reads == 1 && provider.Commands.Count == 1 && clock.Pending == 0,
            "Public StepAsync still performs exactly one observation and at most one command without starting a background refresh");
        await host.StepAsync();
        check(provider.Reads == 2 && provider.Commands.Count == 1 && policy.Calls == 1 && host.LastFault == null,
            "A caller-driven second Step observes independently without replaying the accepted unchanged input");
    }

    private static async Task ForeignHumanWait(Action<bool, string> check)
    {
        var clock = new Clock(); var initial = Frame();
        var human = initial with { State = initial.State with {
            Revision = 2, BoardRevision = 2, Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1, ActivePlayerId = 1,
            Players = [initial.State.Viewer!, new Player(1, "Human", 40, true, false, ImmutableDictionary<string, int>.Empty, [])]
        } };
        var provider = new Port(clock) { Reply = read => read == 1 ? [initial] : [human] };
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        await host.StartAsync(); await Until(() => provider.Reads >= 2 && clock.IdlePending == 1);
        check(provider.Commands.Count == 1 && Accepted(host) == 1 && host.LastFault == null,
            "A fresh human-owned waiting frame retires the bot's accepted-input watchdog without answering for that human");
        clock.Advance(46000); await Until(() => provider.Reads >= 3 && clock.IdlePending == 1);
        check(provider.Commands.Count == 1 && provider.Reports.IsEmpty && host.LastFault == null,
            "Human waiting beyond 45 s remains ordinary idle work rather than a false bot no-progress fault or implicit answer");
    }

    private static int Accepted(AiTableHost host)
        => host.Histories.Sum(history => history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted));
    private static AutomatedSeatObservation Frame(string decision = "accepted-current", long revision = 1, int seat = 0)
        => new(seat, new("ai-loop-game", "playing", null, null, seat, 0, "Pregame", "PREGAME", null,
            [new Player(seat, "Bot", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new(decision, new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = seat
            }, DecisionActionDomain.None, new());
    private static AutomatedSeatObservation Resolving(AutomatedSeatObservation previous, long revision)
        => previous with { State = previous.State with { Revision = revision, BoardRevision = revision,
            Decision = null, DecisionOwnerId = null, ActionClass = "resolving" } };
    private static async Task Until(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, bound.Token);
    }

    private sealed class Policy(AiPriorityAutomation automation = AiPriorityAutomation.FullControl) : IAiPolicy
    {
        private int calls; internal int Calls => Volatile.Read(ref calls); internal bool Fail;
        public AiPolicyDescriptor Descriptor { get; } = new("ai-loop-test.v1", AiTasks.All, automation);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref calls);
            if (Fail) throw new InvalidOperationException("controlled policy failure");
            var state = request.Frame.State;
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "keep-current"));
        }
    }
    private sealed class Port(Clock clock) : IAutomatedSeatProvider, IAutomatedPriorityContinuation
    {
        internal Func<int, ImmutableArray<AutomatedSeatObservation>> Reply = _ => [Frame()];
        internal string Mode = ""; internal int HoldRead;
        internal bool SupportsPriority;
        public bool SupportsAutomatedPriorityContinuation => SupportsPriority;
        public long AutomatedPriorityEpoch => 1;
        private int reads; internal int Reads => Volatile.Read(ref reads);
        internal ConcurrentQueue<long> ReadStarts { get; } = new();
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<string> Reports { get; } = new();
        internal ConcurrentQueue<AutomatedPriorityControl> Controls { get; } = new();
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, GameSnapshot> current = new();
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            int read = Interlocked.Increment(ref reads); ReadStarts.Enqueue(clock.Milliseconds);
            if (read == HoldRead) { Held.TrySetResult(); await Release.Task.WaitAsync(cancellation); }
            if (Mode == "superseded") throw new EngineObservationSupersededException();
            var rows = Reply(read); current.Clear(); foreach (var row in rows) current[row.SeatId] = row.State; return rows;
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (!current.TryGetValue(seatId, out var state) || !GameCommandPermissions.Allows(state, command))
                throw new InvalidDataException("The loop fixture received an unauthorized command");
            Commands.Enqueue(command);
            if (Mode == "rejected" || Mode == "mixed-rejected" && seatId == 1) throw new EngineCommandRejectedException("controlled rejection");
            if (Mode == "unknown" || Mode == "mixed-unknown" && seatId == 1) throw new IOException("controlled unknown receipt");
            return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        { Reports.Enqueue(reasonCode); return Task.CompletedTask; }
        public Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId,
            AutomatedPriorityControl control, CancellationToken cancellation = default)
        {
            control.Validate();
            if (!SupportsPriority || epoch != 1 || !current.TryGetValue(seatId, out var state) || state.Id != control.GameId)
                throw new InvalidDataException("Unowned loop priority control");
            Controls.Enqueue(control);
            return Task.FromResult<AutomatedPriorityControlResult>(new AutomatedPriorityControlResult.Retired());
        }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        internal long Milliseconds { get { lock (gate) return ticks / TimeSpan.TicksPerMillisecond; } }
        internal int Pending { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        internal int IdlePending { get { lock (gate) return timers.Count(timer => timer.Delay == TimeSpan.FromMilliseconds(40) && timer.Due != long.MaxValue); } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(this, callback, state, dueTime, period);
        internal void Advance(int milliseconds)
        {
            List<Timer> ready;
            lock (gate) { ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks; ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            private readonly Clock clock; internal readonly TimerCallback Callback; internal readonly object? State;
            internal long Due; internal TimeSpan Delay, Period; private bool disposed;
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            { this.clock = clock; Callback = callback; State = state; lock (clock.gate) { clock.timers.Add(this); Change(due, period); } }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (clock.gate) { if (disposed) return false; Delay = dueTime; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; Period = period; return true; } }
            public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
