using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class PriorityContinuationRegression
{
    internal static readonly Type[] CommandTypes = [typeof(GrantPriorityContinuation), typeof(RevokePriorityContinuation), typeof(AcknowledgePriorityObservation)];
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var initial = State();
        var grant = new GrantPriorityContinuation(initial.Id, initial.Decision!.Id, 1, "closed-consent", PriorityContinuationMode.NoResponse, ["MAIN1"]);
        check(GameCommandPermissions.Allows(initial, grant), "Ordinary server Auto can register a Held policy while a response is available");
        check(!GameCommandPermissions.Allows(initial, grant with { Sequence = 0 })
            && !GameCommandPermissions.Allows(initial, grant with { PhaseStops = ["MAIN1", "MAIN1"] })
            && !GameCommandPermissions.Allows(initial, grant with { Mode = (PriorityContinuationMode)99 }),
            "Standing consent rejects malformed sequence, duplicate stops and unknown modes");
        check(GameCommandPermissions.Allows(initial, grant with { Mode = PriorityContinuationMode.Quiet, QuietYieldToken = "original-quiet" })
            && !GameCommandPermissions.Allows(initial, grant with { Mode = PriorityContinuationMode.Quiet, QuietYieldToken = "replacement" }),
            "Explicit Quiet consent preserves its exact original engine token");
        check(GameCommandPermissions.Allows(initial with { Decision = null, Status = "finished" }, new RevokePriorityContinuation(initial.Id, 10)),
            "Standing withdrawal is prompt free and remains legal during retirement");
        bool inconsistent = false;
        try { PriorityContinuationFacts.Validate(initial with { Decision = initial.Decision with {
            Payload = ((PriorityPayload)initial.Decision.Payload) with { CanAutoPass = true } } }); }
        catch (ArgumentException) { inconsistent = true; }
        check(inconsistent, "A tri-state response advertisement cannot contradict legacy automatic-pass permission");

        var unsupported = new Port(initial) { SupportsPriorityContinuation = false };
        var legacy = new ClientSession(unsupported); legacy.Accept(initial);
        check(!legacy.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []) && !legacy.PriorityContinuationReserved,
            "A frame without negotiated transport support preserves the legacy runner"); legacy.Close();

        var port = new Port(initial);
        var session = new ClientSession(port); session.Accept(initial);
        check(session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, ["MAIN2"]), "One explicit Auto intent reserves the server dispatcher");
        await Pump(session);
        for (int update = 0; update < 100; update++) {
            session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, ["MAIN2"]); session.Pump();
        }
        check(port.Commands is [GrantPriorityContinuation] && session.PriorityContinuationReserved && !session.PriorityContinuationPending
            && !session.Advance(_ => true, (_, _) => false), "One hundred native updates cannot create a second grant or independent Auto runner");
        session.CancelAdvance();
        check(session.Submit(new PassPriority(initial.Id, initial.Decision.Id)), "A manual gesture is retained while its standing consent is withdrawn");
        await Pump(session);
        check(port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation, PassPriority]
            && port.MaximumConcurrent == 1 && !session.PriorityContinuationReserved,
            "One manual gesture withdraws then answers the exact still-current prompt on the same serial lane");
        session.Close();

        foreach (bool close in new[] { false, true }) {
            port = new(State()) { HoldGrant = true, HoldRevoke = true };
            session = new(port); session.Accept(port.State);
            session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []);
            await port.GrantStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.CancelAdvance();
            var replacement = initial with { Id = "replacement", Revision = 50 };
            check(!session.Accept(replacement), "Take control fences an admitted but unacknowledged grant before audience replacement");
            session.Reset();
            port.ReleaseGrant.TrySetResult();
            await Until(session, () => port.RevokeStarted.Task.IsCompleted);
            check(session.State == null && session.PriorityContinuationWithdrawing && port.MaximumConcurrent == 1,
                "Reset preserves only old-audience negative authority while the grant receipt completes");
            if (close) session.Close();
            port.ReleaseRevoke.TrySetResult();
            if (close) await port.RevokeObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else await Pump(session);
            check(session.State == null && (close || session.Accept(replacement)),
                "A withdrawal receipt cannot resurrect retired presentation and is required before replacement");
            if (!close) session.Close();
        }

        port = new(State()) { FailGrant = true };
        session = new(port); session.Accept(port.State);
        session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []);
        await Until(session, () => session.NeedsRecovery && !session.Busy);
        check(port.Commands is [GrantPriorityContinuation] && session.PriorityContinuationWithdrawing,
            "A lost affirmative receipt leaves control uncertain without positive replay or client fallback");
        session.Poll(false, _ => { }); await Until(session, () => !session.Polling && !session.NeedsRecovery);
        await Pump(session);
        check(port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation] && !session.PriorityContinuationReserved,
            "Recovery observes uncertain consent and withdraws it before restoring manual authority");
        session.Close();

        port = new(State()) { WrongGrantReceipt = true };
        session = new(port); session.Accept(port.State);
        session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []);
        await Until(session, () => session.NeedsRecovery && !session.Busy);
        check(session.PriorityContinuationWithdrawing && port.Commands is [GrantPriorityContinuation],
            "A successful dispatch with the wrong grant receipt becomes uncertain instead of silently retaining pending control");
        session.Poll(false, _ => { }); await Until(session, () => !session.Polling && !session.NeedsRecovery);
        await Pump(session); session.Close();

        port = new(State()); session = new(port); session.Accept(port.State);
        session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []); await Pump(session);
        port.FailRevoke = true; session.Reset();
        await Until(session, () => session.NeedsRecovery && !session.Busy);
        check(session.State == null && session.PriorityContinuationWithdrawing,
            "A lost standing withdrawal after Reset retains old negative authority and retired uncertainty");
        session.RunMatchOperation(() => { port.OtherOperations++; return Task.CompletedTask; });
        await Until(session, () => !session.Busy);
        check(!session.NeedsRecovery && session.State == null && !session.PriorityContinuationWithdrawing && port.OtherOperations == 1,
            "Explicit observed standing-only cleanup clears retired uncertainty without restoring old gameplay");
        session.Close();

        port = new(State() with { PriorityContinuation = new(12, "previous-client", PriorityContinuationMode.NoResponse,
            PriorityContinuationStatus.Held, "response-available", 0, []) });
        session = new(port); session.Accept(port.State); await Pump(session);
        session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []); await Pump(session);
        check(port.Commands is [RevokePriorityContinuation { ThroughSequence: 12 }, GrantPriorityContinuation { Sequence: 13 }],
            "A reconstructed client withdraws orphan consent and advances from the published connection high-water");
        session.RevokePriorityContinuation(); await Pump(session);
        bool contradicted = false;
        try { session.Accept(port.State with { Revision = port.State.Revision + 1,
            PriorityContinuation = port.State.PriorityContinuation! with { Status = PriorityContinuationStatus.Active } }); }
        catch (IOException) { contradicted = true; }
        check(contradicted, "An active instruction cannot return at or below its acknowledged withdrawal fence");
        session.Close();

        var finite = FiniteResolutionRegression.State(3);
        var members = finite.Stack.Select(item => item.ExecutionId).ToImmutableArray();
        finite = finite with { FiniteStackYield = new("recorded-finite", FiniteStackYieldStatus.Active, members, members,
            finite.StackActionVersion ?? 0, finite.ResolutionChoiceVersion ?? 0) };
        foreach (var recorded in new[] { finite, State() with {
            PriorityContinuation = new(5, "recorded-auto", PriorityContinuationMode.NoResponse, PriorityContinuationStatus.Held, "response-available", 0, []),
            PriorityObservation = new(0, 16, 0, 0, false, false) } }) {
            port = new(recorded);
            session = new(port, sourceActions: port, authority: ClientSessionAuthority.ObserveOnly);
            check(session.Accept(recorded) && ReferenceEquals(session.State, recorded),
                "ObserveOnly replay admits the complete recorded grant-bearing immutable frame without rewriting it");
            session.Run(() => { port.OtherOperations++; return Task.CompletedTask; });
            session.RunMatchOperation(() => { port.OtherOperations++; return Task.CompletedTask; });
            session.SourceActions.Observe(new(recorded.Id, "recorded-prompt", recorded.Revision, recorded.ViewerId, "recorded-card", "occurrence"));
            session.CancelAdvance(); session.RevokePriorityContinuation(); session.SetPriorityObservationReady(true);
            check(!session.Submit(new PassPriority(recorded.Id, recorded.Decision!.Id))
                && !session.Advance(_ => true, (_, _) => false)
                && !session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []),
                "ObserveOnly has no manual, automatic or server consent authority");
            session.Pump(); session.Reset(); session.Close();
            check(port.Commands.Count == 0 && port.OtherOperations == 0 && port.SourceReads == 0,
                "Recorded server grants never cause observer gameplay, orphan revocation, metadata ACK or source-action RPCs");
        }
        port = new(State()); session = new(port, authority: ClientSessionAuthority.ObserveOnly);
        bool invalidObserved = false;
        try { session.Accept(port.State with { PriorityContinuation = port.State.PriorityContinuation! with { Status = (PriorityContinuationStatus)99 } }); }
        catch (ArgumentException) { invalidObserved = true; }
        check(invalidObserved, "ObserveOnly preserves the full authority snapshot validators"); session.Close();
        await CleanupAfterObservationFailure(check);
    }

    private static async Task CleanupAfterObservationFailure(Action<bool, string> check)
    {
        foreach (bool retired in new[] { true, false }) {
            var port = new Port(State()); var session = new ClientSession(port); session.Accept(port.State);
            check(session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []),
                "The cleanup-health fixture starts one ordinary standing consent before losing its withdrawal acknowledgement");
            await Pump(session);
            port.FailRevoke = true;
            if (retired) session.Reset(); else session.RevokePriorityContinuation();
            await Until(session, () => session.NeedsRecovery && !session.Busy);
            var origin = session.RecoveryOrigin;
            var retained = session.State;
            check(origin?.Phase == ClientFailurePhase.DispatchUnknown && session.UncertainCommand
                    && session.PriorityContinuationWithdrawing && (session.State == null) == retired
                    && port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation],
                "The lost negative acknowledgement retains command uncertainty and its exact standing withdrawal before a later observation fails");

            var later = new IOException("The cleanup reconciliation observation failed.");
            port.ObserveError = later;
            session.Poll(false, _ => { }); await Until(session, () => !session.Polling);
            check(session.LastFailure is { Phase: ClientFailurePhase.Observation } latest && ReferenceEquals(latest.Cause, later)
                    && ReferenceEquals(session.RecoveryOrigin, origin) && !session.LastFailureWasCommand
                    && session.RecoveryState == ClientRecoveryState.CommandUncertain && port.Commands.Count == 2,
                "A later observation failure updates diagnostics without erasing uncertain withdrawal ownership or resending its command");

            port.ObserveError = null;
            session.RunMatchOperation(() => { port.OtherOperations++; return Task.CompletedTask; });
            await Until(session, () => !session.Busy);
            if (retired) {
                check(session.State == null && session.RecoveryState == ClientRecoveryState.Ready
                        && !session.NeedsRecovery && !session.UncertainCommand && session.RecoveryOrigin == null && session.LastFailure == null
                        && !session.PriorityContinuationWithdrawing && port.OtherOperations == 1
                        && port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation] && port.MaximumConcurrent == 1,
                    "Retired all-ACK cleanup clears authoritative command uncertainty even when the latest failure was an observation");
            } else {
                check(ReferenceEquals(session.State, retained) && session.RecoveryState == ClientRecoveryState.CommandUncertain
                        && session.NeedsRecovery && session.UncertainCommand && ReferenceEquals(session.RecoveryOrigin, origin)
                        && !session.PriorityContinuationWithdrawing && port.OtherOperations == 1
                        && port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation] && port.MaximumConcurrent == 1,
                    "All negative acknowledgements cannot clear an active game's uncertainty or replace its retained authoritative frame");
                session.Poll(false, _ => { }); await Until(session, () => !session.Polling);
                check(ReferenceEquals(session.State, port.State) && session.RecoveryState == ClientRecoveryState.Ready
                        && !session.NeedsRecovery && !session.UncertainCommand && session.RecoveryOrigin == null
                        && port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation],
                    "Only a fresh ordinary frame reconciles the still-active game after negative cleanup without positive or negative replay");
            }
            session.Close();
        }
    }

    private static GameSnapshot State() => new("continuation-game", "playing", null, null, 0, 2, "Main phase", "MAIN1", 1,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
         new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("priority-1", new(), new PriorityPayload(new("Pass once", true), null, false, "original-quiet", []) {
            ResponseAvailability = PriorityResponseAvailability.Available })) {
        Revision = 10, PriorityPlayerId = 0, DecisionOwnerId = 0,
        PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, [])
    };

    private static async Task Pump(ClientSession session)
        => await Until(session, () => !session.Busy && !session.PriorityContinuationPending && !session.PriorityContinuationWithdrawing);
    private static async Task Until(ClientSession session, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do { session.Pump(); await Task.Delay(1); } while (!done() && DateTime.UtcNow < deadline);
        session.Pump();
        if (!done()) throw new TimeoutException("The controlled priority continuation did not release its owner.");
    }

    private sealed class Port(GameSnapshot state) : IMatchConnection, ISourceActions
    {
        internal GameSnapshot State = state;
        public bool SupportsPriorityContinuation { get; set; } = true;
        internal List<GameCommand> Commands = [];
        internal int MaximumConcurrent;
        internal int OtherOperations, SourceReads;
        internal Exception? ObserveError;
        private int concurrent;
        internal bool HoldGrant, HoldRevoke, FailGrant, WrongGrantReceipt, FailRevoke;
        internal TaskCompletionSource GrantStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseGrant = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RevokeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRevoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RevokeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, ++concurrent); Commands.Add(command);
            try {
                if (command is GrantPriorityContinuation grant) {
                    State = State with { Revision = State.Revision + 1, PriorityContinuation = new(grant.Sequence, grant.GrantId,
                        grant.Mode, PriorityContinuationStatus.Held, "response-available", 0, grant.PhaseStops) };
                    GrantStarted.TrySetResult();
                    if (HoldGrant) await ReleaseGrant.Task;
                    if (FailGrant) { FailGrant = false; throw new IOException("Lost grant reply"); }
                } else if (command is RevokePriorityContinuation revoke) {
                    RevokeStarted.TrySetResult();
                    if (HoldRevoke) await ReleaseRevoke.Task;
                    var old = State.PriorityContinuation!;
                    State = State with { Revision = State.Revision + 1, PriorityContinuation = old with {
                        Sequence = Math.Max(old.Sequence, revoke.ThroughSequence),
                        Status = old.Sequence <= revoke.ThroughSequence ? PriorityContinuationStatus.Revoked : old.Status } };
                    if (FailRevoke) { FailRevoke = false; throw new IOException("Lost withdrawal reply"); }
                } else if (command is PassPriority) State = State with { Revision = State.Revision + 1, Decision = null };
                else throw new InvalidOperationException("Unexpected priority continuation command");
            } finally { concurrent--; }
        }
        public Task<GameSnapshot?> ObserveAsync()
        {
            if (ObserveError != null) return Task.FromException<GameSnapshot?>(ObserveError);
            if (State.PriorityContinuation?.Status == PriorityContinuationStatus.Revoked) RevokeObserved.TrySetResult();
            if (WrongGrantReceipt && State.PriorityContinuation?.GrantId.Length > 0) {
                WrongGrantReceipt = false;
                return Task.FromResult<GameSnapshot?>(State with { PriorityContinuation = State.PriorityContinuation with { GrantId = "unrelated-receipt" } });
            }
            return Task.FromResult<GameSnapshot?>(State);
        }
        public Task ConcedeAsync(string game) => Task.CompletedTask;
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            SourceReads++;
            throw new InvalidOperationException("An observation-only session must not query source actions.");
        }
    }
}
