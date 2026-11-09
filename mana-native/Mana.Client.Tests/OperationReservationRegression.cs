using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

// Owned gates expose the wire/owner-queue boundaries without wall-clock sleeps.
internal static class OperationReservationRegression
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await QueuedGestureRetainsCurrentPollAsync(check);
        await SameDecisionDispatchesAfterOwnerApplyAsync(check);
        await RetireUnsentAsync(check);
        await FailedAckRetiresGestureAsync(check);
        await SourceOfferRevalidatedAsync(check);
        await CloseWaitsForQuietWireAsync(check);
        await GenuineReadbackFailureAsync(check);
        await ApplyFailureReleasesOwnerAsync(check);
        await ConnectionRequestsTerminateAsync(check);
        await DiagnosticRetirementCannotAdmitSuccessorAsync(check);
        await FiniteWithdrawalStillDeclinesGestureAsync(check);
    }

    private static async Task QueuedGestureRetainsCurrentPollAsync(Action<bool, string> check)
    {
        var port = new Port(); var session = new ClientSession(port);
        var diagnostics = new List<ClientDiagnostic>(); session.DiagnosticRecorded += diagnostics.Add;
        session.SetPriorityObservationReady(false); session.Accept(port.State);
        session.SetPriorityObservationReady(true);
        try {
            await port.AckStarted.Task.WaitAsync(HangGuard);
            check(session.PriorityObservationInFlight && !session.Busy,
                "A quiet acknowledgement owns its exchange without disabling gameplay controls");
            session.Poll(false, _ => { });
            await port.PollStarted.Task.WaitAsync(HangGuard);
            var pass = new PassPriority(port.State.Id, port.State.Decision!.Id);
            check(session.Submit(pass), "One explicit current gesture is accepted while the quiet wire exchange is held");
            var newer = port.State with { Revision = 11, BoardRevision = 2,
                PriorityObservation = port.State.PriorityObservation! with { BoardRevision = 2 },
                Decision = port.State.Decision with { Id = "new-authority" } };
            port.PollReply.TrySetResult(newer);
            await session.WaitForQueuedWorkAsync().WaitAsync(HangGuard);
            session.Pump();
            check(ReferenceEquals(session.State, newer),
                "A queued unsent gesture cannot advance the operation epoch and discard a current newer ordinary poll");
            port.State = newer;
            port.ReleaseAck.TrySetResult();
            await Settle(session, () => !session.PriorityObservationInFlight && !session.Busy);
            check(port.Commands.ToArray() is [AcknowledgePriorityObservation]
                    && diagnostics.Any(row => row.Stage == ClientDiagnosticStage.LocalDeclined && row.Failure == ClientDiagnosticFailure.NotOffered),
                "The fresh changed decision retires the unsent gesture with an observable local decline and no provider call");
        } finally {
            port.ReleaseAck.TrySetResult(); port.ReleaseGameplay.TrySetResult(); session.Close();
            await port.AckReturned.Task.WaitAsync(HangGuard);
        }
    }

    private static async Task SameDecisionDispatchesAfterOwnerApplyAsync(Action<bool, string> check)
    {
        var port = new Port { HoldFirstPoll = false }; var session = new ClientSession(port);
        port.GameplayOwnerApplied = () => session.State!.PriorityObservation!.Registered && !session.PriorityObservationInFlight;
        session.Accept(port.State); session.Pump();
        try {
            await port.AckStarted.Task.WaitAsync(HangGuard);
            var finite = new FiniteProbe();
            check(!session.AdvanceFiniteCore(finite, (_, _) => false) && finite.Calls == 0 && finite.IsActive,
                "A reserved quiet exchange does not admit, stop or consume a waiting finite policy");
            var pass = new PassPriority(port.State.Id, port.State.Decision!.Id);
            check(session.Submit(pass) && session.Busy && !session.Submit(pass),
                "One bounded gesture is owned with Busy feedback while an additional click is declined");
            port.ReleaseAck.TrySetResult();
            await session.WaitForQueuedWorkAsync().WaitAsync(HangGuard);
            check(port.Commands.ToArray() is [AcknowledgePriorityObservation] && session.Busy,
                "Wire completion alone cannot release the quiet slot or dispatch the queued answer before Pump applies it");
            await Settle(session, () => !session.PriorityObservationInFlight && !session.Busy);
            check(port.Commands.ToArray() is [AcknowledgePriorityObservation, PassPriority]
                    && port.GameplayAfterOwnerApply && session.State!.Decision == null && !session.NeedsRecovery,
                "The same current gesture dispatches once only after the ACK is applied and the owner slot released");
        } finally { port.ReleaseAck.TrySetResult(); port.ReleaseGameplay.TrySetResult(); session.Close(); }
    }

    private static async Task RetireUnsentAsync(Action<bool, string> check)
    {
        foreach (bool failAck in new[] { false, true }) foreach (string change in new[] { "reset", "viewer-ABA", "close" }) {
            string scenario = change + (failAck ? "/failed-ACK" : "/returned-ACK");
            var port = new Port { HoldFirstPoll = false, FailAck = failAck }; var session = new ClientSession(port);
            var diagnostics = new List<ClientDiagnostic>(); session.DiagnosticRecorded += diagnostics.Add;
            session.Accept(port.State); session.Pump();
            try {
                await port.AckStarted.Task.WaitAsync(HangGuard);
                check(session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)), scenario + ": one gesture is queued before retirement");
                session.SetPriorityObservationReady(false);
                if (change == "reset") session.Reset();
                else if (change == "close") session.Close();
                else {
                    session.Accept(port.State with { ViewerId = 1, Revision = 20, Decision = null,
                        Players = port.State.Players.Add(new(1, "Other", 40, true, false, ImmutableDictionary<string, int>.Empty, [])) });
                    session.Accept(port.State with { Revision = 21 });
                }
                check(session.PriorityObservationInFlight && !port.AckReturned.Task.IsCompleted,
                    scenario + ": retiring queued work does not claim that the active metadata wire has completed");
                port.ReleaseAck.TrySetResult();
                if (change == "close") await (failAck ? port.AckReturned.Task : port.AckReadReturned.Task).WaitAsync(HangGuard);
                else await Settle(session, () => !session.PriorityObservationInFlight && !session.Busy);
                check(port.Commands.ToArray() is [AcknowledgePriorityObservation]
                        && diagnostics.Any(row => row.Stage == ClientDiagnosticStage.LocalDeclined
                            && row.Failure == (change == "close" ? ClientDiagnosticFailure.Closed : ClientDiagnosticFailure.Superseded)),
                    scenario + ": an unsent gesture is explicitly retired without I/O or ABA resurrection");
            } finally { port.ReleaseAck.TrySetResult(); port.ReleaseGameplay.TrySetResult(); session.Close(); }
        }
    }

    private static async Task FailedAckRetiresGestureAsync(Action<bool, string> check)
    {
        var port = new Port { HoldFirstPoll = false, FailAck = true }; var session = new ClientSession(port);
        var diagnostics = new List<ClientDiagnostic>(); session.DiagnosticRecorded += diagnostics.Add;
        session.Accept(port.State); session.Pump();
        try {
            await port.AckStarted.Task.WaitAsync(HangGuard);
            check(session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)), "A gesture may wait behind metadata which later fails");
            port.ReleaseAck.TrySetResult();
            await Settle(session, () => !session.PriorityObservationInFlight && !session.Busy);
            check(port.Commands.ToArray() is [AcknowledgePriorityObservation] && !session.NeedsRecovery
                    && diagnostics.Any(row => row.Stage == ClientDiagnosticStage.LocalDeclined && row.Failure == ClientDiagnosticFailure.ObservationFailure),
                "A failed quiet exchange explicitly retires an unsent gesture instead of dispatching against unobserved authority");
        } finally { port.ReleaseAck.TrySetResult(); session.Close(); }
    }

    private static async Task SourceOfferRevalidatedAsync(Action<bool, string> check)
    {
        var port = new Port { HoldFirstPoll = false };
        var card = new Card { Name = "Source", Key = "initial:source", VisualId = "source", CombatId = "source", Selectable = true };
        port.State = port.State with { Players = [port.State.Viewer! with { Zones = [new("Battlefield", 1, [card], null)] }] };
        var session = new ClientSession(port, sourceActions: new SourcePort());
        var diagnostics = new List<ClientDiagnostic>(); session.DiagnosticRecorded += diagnostics.Add;
        session.SetPriorityObservationReady(false); session.Accept(port.State);
        var context = new SourceActionContext(port.State.Id, port.State.Decision!.Id, port.State.Revision, 0, card.Key, card.VisualId);
        session.SourceActions.Observe(context);
        await Settle(session, () => session.SourceActions.View.Status == SourceActionStatus.Ready);
        session.SetPriorityObservationReady(true);
        try {
            await port.AckStarted.Task.WaitAsync(HangGuard);
            check(session.Submit(new UseOfferedAction(port.State.Id, port.State.Decision!.Id, card.Key, "offered")),
                "A currently disclosed exact source offer may be queued behind the quiet exchange");
            port.State = port.State with { Revision = 11 }; session.Accept(port.State);
            port.ReleaseAck.TrySetResult();
            await Settle(session, () => !session.PriorityObservationInFlight && !session.Busy);
            check(port.Commands.ToArray() is [AcknowledgePriorityObservation]
                    && diagnostics.Any(row => row.Stage == ClientDiagnosticStage.LocalDeclined && row.Failure == ClientDiagnosticFailure.SourceOfferChanged),
                "Fresh source permission is rechecked after ACK application without remapping or replaying an old offered action");
        } finally { port.ReleaseAck.TrySetResult(); session.Close(); }
    }

    private static async Task CloseWaitsForQuietWireAsync(Action<bool, string> check)
    {
        var port = new Port { HoldFirstPoll = false }; var session = new ClientSession(port);
        session.Accept(port.State); session.Pump();
        await port.AckStarted.Task.WaitAsync(HangGuard);
        port.State = port.State with { Revision = 11, PriorityContinuation = new(1, "active", PriorityContinuationMode.NoResponse,
            PriorityContinuationStatus.Active, "", 0, []) };
        session.Accept(port.State); session.Close();
        check(port.Commands.ToArray() is [AcknowledgePriorityObservation], "Close cannot overlap its negative cleanup with the held metadata wire");
        port.ReleaseAck.TrySetResult();
        await port.RevocationStarted.Task.WaitAsync(HangGuard);
        check(port.RevocationAfterAckWire && port.Commands.ToArray() is [AcknowledgePriorityObservation, RevokePriorityContinuation],
            "Close tracks the quiet wire task and can withdraw consent after it finishes without requiring an owner Pump");
    }

    private static async Task GenuineReadbackFailureAsync(Action<bool, string> check)
    {
        var port = new Port { HoldFirstPoll = false, FailGameplayRead = true }; var session = new ClientSession(port);
        var failures = new List<ClientOperationFailure>(); session.Failed += failures.Add;
        session.SetPriorityObservationReady(false); session.Accept(port.State);
        check(session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)), "One genuine gameplay exchange is admitted");
        await Settle(session, () => !session.Busy);
        check(failures is [{ Phase: ClientFailurePhase.ReadbackUnknown, Cause: EngineObservationSupersededException }]
                && session.UncertainCommand && session.NeedsRecovery && port.Commands.Count == 1
                && !session.Submit(new PassPriority(port.State.Id, "initial")) && port.Commands.Count == 1,
            "A genuinely superseded post-command readback remains fatal uncertainty with exactly one dispatch and no replay");
        session.Close();
    }

    private static async Task ApplyFailureReleasesOwnerAsync(Action<bool, string> check)
    {
        foreach (bool quiet in new[] { true, false }) {
            var port = new Port { HoldFirstPoll = false }; var session = new ClientSession(port);
            session.SetPriorityObservationReady(false); session.Accept(port.State);
            var expected = new InvalidOperationException("Controlled owner consumer failure");
            session.Changed += (_, _) => throw expected;
            if (quiet) {
                session.SetPriorityObservationReady(true);
                await port.AckStarted.Task.WaitAsync(HangGuard); port.ReleaseAck.TrySetResult();
            } else check(session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)),
                "A foreground apply-failure fixture submits exactly one current command");
            await session.WaitForQueuedWorkAsync().WaitAsync(HangGuard);
            Exception? actual = null;
            try { session.Pump(); } catch (Exception error) { actual = error; }
            check(ReferenceEquals(actual, expected) && !session.Busy && !session.PriorityObservationInFlight && !session.NeedsRecovery,
                (quiet ? "Quiet" : "Foreground") + " owner application failure propagates while finally releasing the exact reservation");
            session.Close();
        }
    }

    private static async Task ConnectionRequestsTerminateAsync(Action<bool, string> check)
    {
        foreach (bool changesMatch in new[] { false, true }) foreach (string end in new[] { "returned", "failed-ACK", "reset", "viewer-ABA", "close" }) {
            string scenario = (changesMatch ? "RunMatchOperation/" : "Run/") + end;
            var port = new Port { HoldFirstPoll = false, FailAck = end == "failed-ACK" };
            var session = new ClientSession(port); session.Accept(port.State); session.Pump();
            int work = 0, completed = 0, reentrantWork = 0; bool activityPending = true, terminalBusy = true;
            var failures = new List<ClientOperationFailure>();
            session.Failed += failure => {
                failures.Add(failure); terminalBusy &= session.Busy;
                session.Run(() => { reentrantWork++; return Task.CompletedTask; });
            };
            session.Completed += () => {
                completed++; activityPending = false;
                if (failures.Count > 0) {
                    terminalBusy &= session.Busy;
                    session.Run(() => { reentrantWork++; return Task.CompletedTask; });
                }
            };
            try {
                await port.AckStarted.Task.WaitAsync(HangGuard);
                Task Work() { work++; return Task.CompletedTask; }
                if (changesMatch) session.RunMatchOperation(Work); else session.Run(Work);
                check(session.Busy && work == 0, scenario + ": the connection request is owned but unsent behind quiet metadata");
                session.SetPriorityObservationReady(false);
                if (end == "reset") session.Reset();
                else if (end == "viewer-ABA") {
                    session.Accept(port.State with { ViewerId = 1, Revision = 20, Decision = null,
                        Players = port.State.Players.Add(new(1, "Other", 40, true, false, ImmutableDictionary<string, int>.Empty, [])) });
                    session.Accept(port.State with { Revision = 21 });
                } else if (end == "close") session.Close();
                if (end is "reset" or "viewer-ABA" or "close") check(completed == 1 && !activityPending && work == 0,
                    scenario + ": lifecycle retirement terminates the loading owner immediately without waiting on the held wire or another Pump");
                port.ReleaseAck.TrySetResult();
                if (end == "close") await port.AckReadReturned.Task.WaitAsync(HangGuard);
                else await Settle(session, () => !session.Busy && !session.PriorityObservationInFlight);
                if (end == "returned") check(work == 1 && completed == 1 && failures.Count == 0 && !activityPending,
                    scenario + ": a current queued connection request executes and completes once after ACK application");
                else check(work == 0 && completed == 1 && !activityPending && reentrantWork == 0 && terminalBusy
                        && failures is [{ Phase: ClientFailurePhase.DispatchRejected, Cause: EngineCommandRejectedException }]
                        && !session.NeedsRecovery && !session.UncertainCommand,
                    scenario + ": one typed local rejection and completion retire unsent Work without I/O, uncertainty or a reentrant successor");
                check(port.Commands.ToArray() is [AcknowledgePriorityObservation], scenario + ": connection Work retirement never adds a provider command");
            } finally { port.ReleaseAck.TrySetResult(); session.Close(); }
        }
    }

    private static async Task DiagnosticRetirementCannotAdmitSuccessorAsync(Action<bool, string> check)
    {
        foreach (bool close in new[] { false, true }) {
            var port = new Port { HoldFirstPoll = false }; var session = new ClientSession(port);
            session.Accept(port.State); session.Pump();
            int retired = 0, completed = 0, reentrant = 0; bool refused = false, sawTerminalBusy = false;
            var reason = close ? ClientDiagnosticFailure.Closed : ClientDiagnosticFailure.Superseded;
            var pass = new PassPriority(port.State.Id, port.State.Decision!.Id);
            session.DiagnosticRecorded += row => {
                if (row.Stage != ClientDiagnosticStage.LocalDeclined || row.Failure != reason) return;
                retired++; sawTerminalBusy = session.Busy;
                session.Run(() => { reentrant++; return Task.CompletedTask; });
                refused = !session.Submit(pass);
            };
            session.Completed += () => completed++;
            var releaseNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try {
                await port.AckStarted.Task.WaitAsync(HangGuard);
                check(session.Submit(pass), "Diagnostic retirement: one exact gesture is queued before the lifecycle fence");
                session.SetPriorityObservationReady(false);
                if (close) session.Close(); else session.Reset();
                check(retired == 1 && completed == 1 && sawTerminalBusy && refused && reentrant == 0
                        && port.Commands.ToArray() is [AcknowledgePriorityObservation],
                    "A reentrant retirement diagnostic cannot admit old-audience work or manufacture another pending gesture");
                if (close) {
                    session.Close(); port.ReleaseAck.TrySetResult();
                    await port.AckReadReturned.Task.WaitAsync(HangGuard);
                    check(completed == 1 && reentrant == 0, "Close and its late quiet completion cannot repeat the retired gesture's completion");
                } else {
                    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    session.Run(async () => { started.TrySetResult(); await releaseNew.Task; });
                    check(session.Busy && completed == 1 && !started.Task.IsCompleted,
                        "A separately accepted successor waits behind the actual old wire, without borrowing its completion");
                    port.ReleaseAck.TrySetResult();
                    await session.WaitForQueuedWorkAsync().WaitAsync(HangGuard); session.Pump();
                    await started.Task.WaitAsync(HangGuard);
                    check(completed == 1, "The old ACK application releases its own token without completing the newer connection request");
                    releaseNew.TrySetResult(); await Settle(session, () => !session.Busy);
                    check(completed == 2 && reentrant == 0, "Only the newer request's own application completes that successor");
                }
            } finally { releaseNew.TrySetResult(); port.ReleaseAck.TrySetResult(); session.Close(); }
        }
    }

    private static async Task FiniteWithdrawalStillDeclinesGestureAsync(Action<bool, string> check)
    {
        var port = new OrphanPort(); var session = new ClientSession(port);
        var diagnostics = new List<ClientDiagnostic>(); session.DiagnosticRecorded += diagnostics.Add;
        session.Accept(port.State);
        check(!session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)) && !session.Busy
                && diagnostics.Any(row => row.Stage == ClientDiagnosticStage.LocalDeclined && row.Failure == ClientDiagnosticFailure.FiniteRevocationPending),
            "An advertised prior-client finite grant still rejects positive gameplay instead of deferring it through withdrawal");
        await Settle(session, () => !session.Busy);
        check(port.Commands.ToArray() is [RevokeFiniteStackYield] && port.State.FiniteStackYield!.Status == FiniteStackYieldStatus.Revoked,
            "Finite orphan cleanup sends only its exact negative command and cannot later replay the declined gesture");
        session.Close();
    }

    private sealed class OrphanPort : IMatchConnection
    {
        internal GameSnapshot State = FiniteResolutionRegression.State(1) with {
            FiniteStackYield = new("prior-client", FiniteStackYieldStatus.Active, ["execution-0"], ["execution-0"], 1, 1)
        };
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (command is not RevokeFiniteStackYield) throw new InvalidOperationException("A declined gesture reached the orphan port");
            State = State with { Revision = State.Revision + 1, FiniteStackYield = State.FiniteStackYield! with { Status = FiniteStackYieldStatus.Revoked } };
            return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string game) => throw new InvalidOperationException("The orphan fixture never concedes");
    }

    private static async Task Settle(ClientSession session, Func<bool> done)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        while (true) { session.Pump(); if (done()) return; await session.WaitForQueuedWorkAsync(guard.Token); }
    }

    private sealed class FiniteProbe : IFiniteDecisionPolicy
    {
        internal int Calls;
        public bool IsActive { get; private set; } = true;
        public bool CanContinue(GameSnapshot state) { Calls++; return true; }
        public FiniteNextStep NextStep(GameSnapshot state) { Calls++; return FiniteNextStep.Observe; }
        public void Submitted(GameSnapshot state, DecisionCommand command) { Calls++; }
        public void Stop() { Calls++; IsActive = false; }
    }
    private sealed class SourcePort : ISourceActions
    {
        public bool SupportsSourceActions => true;
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string key)
            => Task.FromResult(new SourceActionSet(gameId, decisionId, key, "source", [new("offered", "activate", "Read exact source")]));
    }

    private sealed class Port : IMatchConnection
    {
        internal GameSnapshot State = new("operation-slot", "playing", null, null, 0, 2, "Main phase", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("initial", new(), new PriorityPayload(new("Pass once", true)))) {
                Revision = 10, BoardRevision = 1,
                PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                PriorityObservation = new(1, 0, 0, 0, false, false)
            };
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal readonly TaskCompletionSource AckStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AckReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PollStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<GameSnapshot?> PollReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseGameplay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        internal bool HoldFirstPoll = true, HoldGameplay, FailAck, FailGameplayRead;
        internal Func<bool>? GameplayOwnerApplied;
        internal bool GameplayAfterOwnerApply, RevocationAfterAckWire;
        internal readonly TaskCompletionSource AckReadReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RevocationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsPriorityContinuation => true;
        public async Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (command is AcknowledgePriorityObservation ack) {
                AckStarted.TrySetResult(); await ReleaseAck.Task;
                if (FailAck) { AckReturned.TrySetResult(); throw new IOException("Controlled metadata failure"); }
                State = State with { PriorityObservation = State.PriorityObservation! with {
                    Registered = true, AcknowledgedBoardRevision = ack.BoardRevision,
                    AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } };
                AckReturned.TrySetResult();
            } else if (command is PassPriority) {
                GameplayAfterOwnerApply = GameplayOwnerApplied?.Invoke() ?? false;
                if (HoldGameplay || HoldFirstPoll) await ReleaseGameplay.Task;
                State = State with { Revision = State.Revision + 1, Decision = null };
            } else if (command is RevokePriorityContinuation revoke) {
                RevocationAfterAckWire = AckReadReturned.Task.IsCompleted;
                State = State with { Revision = State.Revision + 1,
                    PriorityContinuation = State.PriorityContinuation! with { Sequence = revoke.ThroughSequence, Status = PriorityContinuationStatus.Revoked } };
                RevocationStarted.TrySetResult();
            } else throw new InvalidOperationException("Unexpected operation-reservation command");
        }
        public async Task<GameSnapshot?> ObserveAsync()
        {
            if (Interlocked.Increment(ref reads) == 1 && HoldFirstPoll) {
                PollStarted.TrySetResult(); return await PollReply.Task;
            }
            if (FailGameplayRead) throw new EngineObservationSupersededException();
            if (State.PriorityObservation!.Registered) AckReadReturned.TrySetResult();
            return State;
        }
        public Task ConcedeAsync(string game) => throw new InvalidOperationException("The controlled operation never concedes");
    }
}
