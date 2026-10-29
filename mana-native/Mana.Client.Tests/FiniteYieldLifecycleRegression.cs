using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class FiniteYieldLifecycleRegression
{
    internal static readonly Type[] CommandTypes = [typeof(GrantFiniteStackYield), typeof(RevokeFiniteStackYield)];
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Authority(check);
        ProposedGrantCancellation(check);
        await ServerPasses(check);
        await PauseAndResume(check);
        await ResetAndClose(check);
        await Recovery(check, revokeFails: false);
        await Recovery(check, revokeFails: true);
        await Orphan(check);
        await MatchOperations(check);
        await MalformedRevocation(check);
        await CloseUncertainRevoke(check);
        await ReplacedRevocation(check);
        await SwitchingAdvance(check);
        await MatchOperationReset(check);
        await ContradictoryAcknowledgement(check);
        await RevocationAudienceAndRevision(check);
    }

    private static void ProposedGrantCancellation(Action<bool, string> check)
    {
        var state = new Port(3, automatic: false).State;
        var intent = FiniteResolutionRegression.Arm(state)!;
        var policy = (IFiniteDecisionPolicy)intent;
        var proposed = (GrantFiniteStackYield)policy.NextCommand(state)!;
        // Deterministically pause at the runner's cancellation check between
        // obtaining the command and ExecuteAsync: no command was dispatched.
        intent.Pause();
        var revoke = intent.RequestServerRevocation()!;
        intent.Resume();
        check(intent.CanContinue(state) && policy.NextCommand(state) == null,
            "An ordinary Idle projection cannot erase a proposed finite grant while its negative acknowledgement remains pending");
        intent.AcknowledgeServerRevocation(state, revoke with { GrantId = "other-proposal" });
        check(policy.NextCommand(state) == null,
            "A different proposal's negative acknowledgement cannot clear the captured prospective grant");
        var active = state with { FiniteStackYield = new(proposed.GrantId, FiniteStackYieldStatus.Active,
            proposed.ExecutionIds, proposed.ExecutionIds, proposed.StackActionVersion, proposed.ResolutionChoiceVersion) };
        intent.AcknowledgeServerRevocation(active, revoke);
        check(policy.NextCommand(state) == null,
            "An acknowledgement still advertising the exact active grant cannot permit another grant or local pass");
        FiniteStackYieldFacts.Validate(state);
        intent.AcknowledgeServerRevocation(state, revoke);
        var resumed = policy.NextCommand(state) as GrantFiniteStackYield;
        check(resumed != null && resumed.GrantId != proposed.GrantId && resumed.ExecutionIds.SequenceEqual(proposed.ExecutionIds)
                && resumed.StackActionVersion == proposed.StackActionVersion && resumed.ResolutionChoiceVersion == proposed.ResolutionChoiceVersion
                && intent.Progress is { CapturedCount: 3, CompletedCount: 0, RemainingCount: 3 },
            "A validated Idle revoke receipt clears an unsent proposal so deliberate resume creates a fresh grant for the same finite members and fences, without inventing outcomes");
    }

    private static async Task MatchOperationReset(Action<bool, string> check)
    {
        foreach (bool resetBeforeAckPump in new[] { false, true }) {
            var port = new Port(3, automatic: false);
            var session = new ClientSession(port); session.Accept(port.State);
            var intent = FiniteResolutionRegression.Arm(port.State)!;
            session.AdvanceFinite(intent, (_, _) => false);
            await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Pump(session);
            int failures = 0; session.Failed += _ => failures++;
            var operationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.RunMatchOperation(() => {
                port.DisallowCommands = true; // The leave/replace delegate retires the old connection.
                if (!resetBeforeAckPump) session.Post(session.Reset);
                operationDone.TrySetResult(); return Task.CompletedTask;
            });
            await operationDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (resetBeforeAckPump) session.Reset();
            await Pump(session);
            check(session.State == null && !session.NeedsRecovery && failures == 0
                    && port.Commands.Count == 2 && port.Commands[1] is RevokeFiniteStackYield,
                resetBeforeAckPump
                    ? "A queued match reset before ACK publication deduplicates its existing negative capability without dispatching after the connection leaves"
                    : "A queued match reset after ACK publication cannot re-add the acknowledged grant from the deliberately retained old Active replica");
            session.Close();
        }
    }

    private static async Task ContradictoryAcknowledgement(Action<bool, string> check)
    {
        var port = new Port(3, automatic: false);
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        session.AdvanceFinite(intent, (_, _) => false);
        await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Pump(session);
        session.RunMatchOperation(() => Task.CompletedTask); await Pump(session);
        var retained = session.State;
        var contradictory = port.State with { Revision = port.State.Revision + 1,
            FiniteStackYield = port.State.FiniteStackYield! with { Status = FiniteStackYieldStatus.Active } };
        check(!session.Accept(contradictory) && session.State == retained && session.NeedsRecovery && session.UncertainCommand
                && !session.Submit(new PassPriority(contradictory.Id, contradictory.Decision!.Id)),
            "A higher-revision same-ID Active grant contradicting a validated revoke ACK requires uncertain-ownership recovery rather than using suppression to authorize work");
        check(session.Accept(port.State) && !session.NeedsRecovery && session.State?.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked,
            "A fresh valid terminal observation can recover contradictory ownership without granting or replaying any affirmative action");
        session.Close();
    }

    private static async Task RevocationAudienceAndRevision(Action<bool, string> check)
    {
        foreach (bool reset in new[] { false, true })
        foreach (bool foreignViewer in new[] { false, true }) {
            var port = new Port(3, automatic: false);
            var session = new ClientSession(port); session.Accept(port.State);
            var intent = FiniteResolutionRegression.Arm(port.State)!;
            session.AdvanceFinite(intent, (_, _) => false);
            await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Pump(session);
            port.ForeignRevokeReceipt = foreignViewer; port.StaleRevokeReceipt = !foreignViewer;
            bool changed = false;
            if (reset) session.Reset();
            else session.RunMatchOperation(() => { changed = true; return Task.CompletedTask; });
            await Pump(session);
            check(!changed && session.NeedsRecovery && session.UncertainCommand
                    && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1 && (!reset || session.State == null),
                (foreignViewer ? "A foreign-viewer" : "A stale terminal") + " revoke receipt cannot acknowledge captured grant ownership"
                    + (reset ? " after Reset has cleared the presentation replica" : " or invoke dependent match work"));
            port.ForeignRevokeReceipt = port.StaleRevokeReceipt = false;
            if (reset) {
                check(!session.Accept(port.State) && session.State == null && session.NeedsRecovery,
                    "A reset game remains retired even when its matching terminal receipt becomes available");
                session.RunMatchOperation(() => Task.CompletedTask); await Pump(session);
                check(!session.NeedsRecovery && !session.UncertainCommand && session.State == null
                        && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1,
                    "Explicit negative cleanup can observe every pending revoke and clear only retired cleanup uncertainty without restoring its game or resending a command");
            } else {
                session.Accept(port.State); await Pump(session);
                check(!session.NeedsRecovery && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1,
                    "A fresh matching-audience terminal receipt recovers the uncertain negative command without blindly sending it again");
            }
            session.Close();
        }
    }

    private static void Authority(Action<bool, string> check)
    {
        var state = new Port(3, automatic: false).State;
        var grant = new GrantFiniteStackYield(state.Id, state.Decision!.Id, "explicit-grant",
            state.Stack.Select(item => item.ExecutionId).ToImmutableArray(), 1, 1);
        grant.Validate();
        check(GameCommandPermissions.Allows(state, grant)
                && !GameCommandPermissions.Allows(state with { FiniteStackYield = null }, grant)
                && !GameCommandPermissions.Allows(state with { DecisionOwnerId = 1 }, grant)
                && !GameCommandPermissions.Allows(state, grant with { DecisionId = "stale-input" })
                && !GameCommandPermissions.Allows(state, grant with { ExecutionIds = grant.ExecutionIds.RemoveAt(0) })
                && !GameCommandPermissions.Allows(state, grant with { StackActionVersion = 2 })
                && !GameCommandPermissions.Allows(state, grant with { ResolutionChoiceVersion = 2 }),
            "A finite grant requires advertised support, current own priority, exact entire current execution queue and both current fences");
        state = state with { FiniteStackYield = new(grant.GrantId, FiniteStackYieldStatus.Active, grant.ExecutionIds, grant.ExecutionIds, 1, 1) };
        var revoke = new RevokeFiniteStackYield(state.Id, grant.GrantId); revoke.Validate();
        check(GameCommandPermissions.Allows(state with { Decision = null, DecisionOwnerId = 1 }, revoke)
                && !GameCommandPermissions.Allows(state, revoke with { GrantId = "someone-elses-grant" })
                && !GameCommandPermissions.Allows(state, revoke with { GameId = "other-game" }),
            "Only the exact advertised grant can be revoked out of prompt; a negative capability grants no unrelated input authority");
    }

    private static async Task MatchOperations(Action<bool, string> check)
    {
        foreach (bool lostAck in new[] { false, true }) {
            var port = new Port(3, automatic: false);
            var session = new ClientSession(port); session.Accept(port.State);
            var intent = FiniteResolutionRegression.Arm(port.State)!;
            session.AdvanceFinite(intent, (_, _) => false);
            await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Yield the bounded worker while keeping its consent alive, as a
            // normal foreground read must not alter the match's chosen pace.
            await Pump(session);
            bool read = false;
            session.Run(() => { read = true; return Task.CompletedTask; }); await Pump(session);
            check(read && intent.IsActive && port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Active
                    && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 0,
                "A catalog/setup read uses the existing work lane without withdrawing finite match consent");
            port.FailRevoke = lostAck;
            bool changed = false, revokedFirst = false;
            session.RunMatchOperation(() => {
                changed = true; revokedFirst = port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked;
                return Task.CompletedTask;
            }); await Pump(session);
            check(!intent.IsActive && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1
                    && port.MaximumConcurrentCommands == 1 && (lostAck ? !changed && session.NeedsRecovery : changed && revokedFirst),
                lostAck
                    ? "A lost revoke acknowledgement prevents the dependent leave/reconnect operation and keeps ordinary recovery authority"
                    : "An explicit match operation observes withdrawal of its exact grant before invoking leave, concede or reconnect on the same lane");
            if (lostAck) { session.Accept(port.State); await Pump(session); }
            session.Close();
        }
    }

    private static async Task MalformedRevocation(Action<bool, string> check)
    {
        var port = new Port(3, automatic: false);
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        session.AdvanceFinite(intent, (_, _) => false);
        await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Pump(session);
        port.MalformedRevoke = true;
        bool changed = false;
        session.RunMatchOperation(() => { changed = true; return Task.CompletedTask; }); await Pump(session);
        check(!changed && session.NeedsRecovery && session.UncertainCommand
                && session.State?.FiniteStackYield?.Status == FiniteStackYieldStatus.Active
                && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1,
            "A malformed revocation receipt cannot clear negative consent or invoke a dependent match operation before ordinary typed validation");
        port.MalformedRevoke = false;
        session.Accept(port.State); await Pump(session); session.Close();
    }

    private static async Task CloseUncertainRevoke(Action<bool, string> check)
    {
        foreach (bool committed in new[] { false, true }) {
            var port = new Port(3, automatic: false) { FailRevoke = committed, FailRevokeBeforeCommit = !committed };
            var session = new ClientSession(port); session.Accept(port.State);
            var intent = FiniteResolutionRegression.Arm(port.State)!;
            session.AdvanceFinite(intent, (_, _) => false);
            await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            intent.Pause(); session.CancelAdvance(); await Pump(session);
            check(session.NeedsRecovery && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1,
                "The close regression starts with an actually uncertain revocation send, without a fabricated recovered acknowledgement");
            int traceStart = port.Trace.Count;
            session.Close();
            await port.CloseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked && port.MaximumConcurrentCommands == 1
                    && port.Commands.OfType<RevokeFiniteStackYield>().Count() == (committed ? 1 : 2)
                    && port.Trace[traceStart].StartsWith("observe:", StringComparison.Ordinal),
                committed
                    ? "Closing after a lost committed revoke ACK first observes its terminal grant and never blindly resends the negative command"
                    : "Closing after an uncommitted uncertain revoke first observes the exact still-active grant before one grounded idempotent retry");
        }
    }

    private static async Task ReplacedRevocation(Action<bool, string> check)
    {
        var port = new Port(3, automatic: false) { ReplaceGrantOnRevoke = true };
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        session.AdvanceFinite(intent, (_, _) => false);
        await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Pump(session);
        bool changed = false;
        session.RunMatchOperation(() => { changed = true; return Task.CompletedTask; }); await Pump(session);
        check(!changed && session.NeedsRecovery && session.UncertainCommand
                && port.State.FiniteStackYield is { Status: FiniteStackYieldStatus.Active, GrantId: "replacement-grant" }
                && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1,
            "A receipt advertising a different active grant is changed ownership, not a successful revoke ACK authorizing dependent match work");
        session.Close();
    }

    private static async Task SwitchingAdvance(Action<bool, string> check)
    {
        var port = new Port(3, automatic: false); port.OfferAuto();
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        session.AdvanceFinite(intent, (_, _) => false);
        await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Pump(session);
        check(GameCommandPermissions.Allows(session.State!, new AutoPassPriority(session.State!.Id, session.State.Decision!.Id))
                && !session.Advance(_ => true, (_, _) => false),
            "Switching from a supported finite grant to otherwise offered Auto first stops the old consent and refuses to dispatch before its newly queued revoke");
        await Pump(session);
        check(!intent.IsActive && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1
                && port.Commands.OfType<AutoPassPriority>().Count() == 0 && port.MaximumConcurrentCommands == 1,
            "Replacing advance policies cannot bypass the exact revocation queued while retiring the previous policy");
        session.Close();

        var ordinary = new Port(1, automatic: false); ordinary.OfferAuto();
        var legacy = new ClientSession(ordinary); legacy.Accept(ordinary.State);
        check(legacy.Advance(_ => true, (_, _) => false), "An otherwise identical offered Auto remains available with no active finite consent");
        await Pump(legacy);
        check(ordinary.Commands.SingleOrDefault() is AutoPassPriority && !legacy.NeedsRecovery,
            "The finite revoke fence preserves ordinary fresh Auto command execution when no grant is being replaced");
        legacy.Close();
    }

    private static async Task ServerPasses(Action<bool, string> check)
    {
        var port = new Port(55, automatic: true);
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        int terminals = 0;
        session.Changed += (_, _) => terminals += session.ExecutionChanges.Transitions.Count(item => item.Step == ExecutionStep.Complete);
        for (int batch = 0; intent.IsActive && batch < 16; batch++) {
            session.AdvanceFinite(intent, (_, _) => false); await Pump(session);
        }
        check(!intent.IsActive && intent.Progress is { CompletedCount: 55, ResolvedCount: 55 } && terminals == 55
                && port.Commands.OfType<GrantFiniteStackYield>().Count() == 1 && port.Commands.OfType<PassPriority>().Count() == 0
                && port.AutomaticPasses == 55,
            "A supported finite queue uses one explicit grant and actual provider priority passes while observing all55terminal outcomes, without55client pass roundtrips");
        check(port.Commands.OfType<GrantFiniteStackYield>().Single() is { ExecutionIds.Length: 55 }
                && !session.NeedsRecovery && port.MaximumConcurrentCommands == 1,
            "The provider grant captures exactly the current complete queue and keeps the existing serialized command lane");
        session.Close();

        var capped = new Port(FiniteStackYieldFacts.MaximumMembers + 1, automatic: false);
        var legacy = new ClientSession(capped); legacy.Accept(capped.State);
        var many = FiniteResolutionRegression.Arm(capped.State)!;
        legacy.AdvanceFinite(many, (before, after) => after.Revision > before.Revision); await Pump(legacy);
        check(capped.Commands.Count == 1 && capped.Commands[0] is PassPriority && many.Progress.CompletedCount == 1,
            "A queue beyond the provider's explicit grant bound retains ordinary fresh client passes instead of widening its capability");
        legacy.Close();
    }

    private static async Task PauseAndResume(Action<bool, string> check)
    {
        var port = new Port(3, automatic: false);
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        session.AdvanceFinite(intent, (_, _) => false);
        await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        intent.Pause(); session.CancelAdvance(); await Pump(session);
        var first = port.Commands.OfType<GrantFiniteStackYield>().Single();
        check(port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked && intent.IsActive && intent.IsPaused
                && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1 && port.Commands.OfType<PassPriority>().Count() == 0,
            "Hover pause withdraws the server grant after reconciling its in-flight receipt, while retaining finite local consent and never issuing a duplicate local pass");
        intent.Resume();
        check(session.AdvanceFinite(intent, (_, _) => false), "A deliberately resumed finite queue can obtain a fresh current grant after revocation is observed");
        await port.SecondGrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        intent.Pause(); session.CancelAdvance(); await Pump(session);
        check(port.Commands.OfType<GrantFiniteStackYield>().Select(command => command.GrantId).Distinct().Count() == 2
                && port.Commands.OfType<RevokeFiniteStackYield>().Select(command => command.GrantId).Contains(first.GrantId)
                && port.MaximumConcurrentCommands == 1,
            "Resume uses a new grant identity and exact current scope rather than silently reactivating the revoked provider consent");
        session.Close();
    }

    private static async Task ResetAndClose(Action<bool, string> check)
    {
        foreach (bool close in new[] { false, true }) {
            var port = new Port(3, automatic: false) { HoldGrant = true };
            var session = new ClientSession(port); session.Accept(port.State);
            var intent = FiniteResolutionRegression.Arm(port.State)!;
            int publications = 0; session.Changed += (_, _) => publications++;
            session.AdvanceFinite(intent, (_, _) => false);
            await port.GrantStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (close) session.Close(); else session.Reset();
            int baseline = publications;
            port.ReleaseGrant.TrySetResult();
            if (close) await port.RevokeObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); else await Pump(session);
            check(!intent.IsActive && port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked
                    && port.Commands.Count == 2 && port.Commands[0] is GrantFiniteStackYield && port.Commands[1] is RevokeFiniteStackYield
                    && port.MaximumConcurrentCommands == 1 && publications == baseline && (close || session.State == null),
                close
                    ? "Closing chains one best-effort revoke after the actual sent command on the same lane and never publishes the old scope"
                    : "Reset preserves the exact negative grant capability until its serialized revoke is observed, without restoring the reset game");
            if (!close) session.Close();
        }
    }

    private static async Task Recovery(Action<bool, string> check, bool revokeFails)
    {
        var port = new Port(3, automatic: false) { FailGrant = !revokeFails, FailRevoke = revokeFails };
        var session = new ClientSession(port); session.Accept(port.State);
        var intent = FiniteResolutionRegression.Arm(port.State)!;
        session.AdvanceFinite(intent, (_, _) => false);
        if (revokeFails) {
            await port.GrantObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            intent.Pause(); session.CancelAdvance();
        }
        await Pump(session);
        int before = port.Commands.Count;
        check(session.NeedsRecovery && session.UncertainCommand && !intent.IsActive
                && !session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)),
            "A lost " + (revokeFails ? "revocation" : "grant") + " acknowledgement requires recovery and cannot permit a new gameplay command");
        session.Pump();
        check(port.Commands.Count == before, "An uncertain finite-grant operation is never retried without a fresh provider observation");
        session.Accept(port.State); await Pump(session);
        check(!session.NeedsRecovery && port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked
                && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1,
            "Recovery observes the exact grant state, clears orphan consent when needed and does not resend an already committed revoke");
        session.Close();
    }

    private static async Task Orphan(Action<bool, string> check)
    {
        var port = new Port(3, automatic: false);
        var grant = new GrantFiniteStackYield(port.State.Id, port.State.Decision!.Id, "orphan-from-old-client",
            port.State.Stack.Select(item => item.ExecutionId).ToImmutableArray(), 1, 1);
        await port.ExecuteAsync(grant);
        var session = new ClientSession(port); session.Accept(port.State);
        check(!session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)),
            "A newly attached client cannot issue gameplay while an advertised grant from a prior client remains active");
        await Pump(session);
        check(port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked && port.Commands.Count == 2
                && port.Commands[1] is RevokeFiniteStackYield,
            "An orphan grant is withdrawn through its exact advertised identity before the new client restores manual control");
        session.Close();
    }

    private static async Task Pump(ClientSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do { session.Pump(); await Task.Delay(1); } while (session.Busy && DateTime.UtcNow < deadline);
        session.Pump();
        if (session.Busy) throw new TimeoutException("Finite grant/revocation did not release its serial lane.");
    }

    private sealed class Port(int count, bool automatic) : IMatchConnection
    {
        private readonly object gate = new();
        private GameSnapshot state = Supported(FiniteResolutionRegression.State(count));
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal List<GameCommand> Commands { get; } = [];
        internal List<string> Trace { get; } = [];
        internal int AutomaticPasses, MaximumConcurrentCommands;
        private int concurrent, grants;
        internal bool HoldGrant, FailGrant, FailRevoke, FailRevokeBeforeCommit, MalformedRevoke, ReplaceGrantOnRevoke, DisallowCommands,
            ForeignRevokeReceipt, StaleRevokeReceipt;
        private bool uncertainRevoke;
        internal TaskCompletionSource CloseObservation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource GrantStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseGrant { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource GrantObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondGrantObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RevokeObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void OfferAuto()
        {
            lock (gate) state = state with { Decision = state.Decision! with {
                Payload = ((PriorityPayload)state.Decision.Payload) with { CanAutoPass = true } } };
        }
        public async Task ExecuteAsync(GameCommand command)
        {
            lock (gate) { MaximumConcurrentCommands = Math.Max(MaximumConcurrentCommands, ++concurrent); Commands.Add(command); Trace.Add("execute:" + command.GetType().Name); }
            try {
                if (DisallowCommands) throw new IOException("The match operation retired this connection.");
                if (command is GrantFiniteStackYield grant) {
                    lock (gate) { grants++; state = state with { Revision = state.Revision + 1,
                        FiniteStackYield = new(grant.GrantId, FiniteStackYieldStatus.Active, grant.ExecutionIds, grant.ExecutionIds, 1, 1) }; }
                    GrantStarted.TrySetResult();
                    if (HoldGrant) await ReleaseGrant.Task;
                    if (FailGrant) { FailGrant = false; throw new IOException("Lost grant acknowledgement"); }
                } else if (command is RevokeFiniteStackYield revoke) {
                    if (FailRevokeBeforeCommit) { FailRevokeBeforeCommit = false; uncertainRevoke = true; throw new IOException("Lost before revoke commit"); }
                    lock (gate) {
                        if (state.FiniteStackYield!.GrantId != revoke.GrantId) throw new EngineCommandRejectedException("Different grant");
                        state = state with { Revision = state.Revision + 1,
                            FiniteStackYield = ReplaceGrantOnRevoke
                                ? state.FiniteStackYield with { GrantId = "replacement-grant" }
                                : state.FiniteStackYield with { Status = FiniteStackYieldStatus.Revoked } };
                    }
                    if (FailRevoke) { FailRevoke = false; uncertainRevoke = true; throw new IOException("Lost revoke acknowledgement"); }
                } else if (command is PassPriority or AutoPassPriority) {
                    lock (gate) {
                        int completed = state.Executions!.Occurrences.Count(item => item.Step == ExecutionStep.Complete) + 1;
                        state = Supported(FiniteResolutionRegression.State(count, completed: completed, revision: state.Revision + 1));
                    }
                } else throw new InvalidOperationException("Unexpected finite-yield command.");
            } finally { lock (gate) concurrent--; }
        }
        public Task<GameSnapshot?> ObserveAsync()
        {
            lock (gate) {
                Trace.Add("observe:" + state.FiniteStackYield?.Status);
                if (state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } grant) {
                    if (automatic) {
                        int completed = ++AutomaticPasses;
                        state = FiniteResolutionRegression.State(count, completed: completed, revision: state.Revision + 1) with {
                            FiniteStackYield = grant with { Status = completed == count ? FiniteStackYieldStatus.Completed : FiniteStackYieldStatus.Active,
                                RemainingExecutionIds = grant.ExecutionIds.Skip(completed).ToImmutableArray() } };
                    }
                    (grants == 1 ? GrantObserved : SecondGrantObserved).TrySetResult();
                }
                if (state.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked) RevokeObserved.TrySetResult();
                if (uncertainRevoke && state.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked) CloseObservation.TrySetResult();
                var observed = state;
                if (state.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked) {
                    if (MalformedRevoke) observed = observed with { FiniteStackYield = state.FiniteStackYield with { Status = (FiniteStackYieldStatus)99 } };
                    if (ForeignRevokeReceipt) observed = observed with { ViewerId = 1 };
                    if (StaleRevokeReceipt) observed = observed with { Revision = 1 };
                }
                return Task.FromResult<GameSnapshot?>(observed);
            }
        }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
        private static GameSnapshot Supported(GameSnapshot snapshot)
            => snapshot with { FiniteStackYield = new("", FiniteStackYieldStatus.Idle, [], [], 1, 1) };
    }
}
