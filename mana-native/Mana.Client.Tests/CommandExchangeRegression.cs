using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class CommandExchangeRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (bool diagnostics in new[] { false, true }) {
            foreach (string mode in new[] { "direct", "source", "auto", "quiet", "finite", "optional" }) {
                await Readback(mode, diagnostics, check);
                await FirstRejected(mode, diagnostics, check);
            }
            foreach (string mode in new[] { "auto", "finite" }) await WaitWithoutCommand(mode, diagnostics, check);
            foreach (string mode in new[] { "auto", "quiet" }) await BeforeContinuation(mode, diagnostics, check);
            foreach (string mode in new[] { "auto", "quiet" }) await NewOwnerBeforeContinuation(mode, diagnostics, check);
            foreach (string mode in new[] { "auto", "quiet", "stack" }) await PartiallyCommittedBatch(mode, diagnostics, check);
            foreach (string mode in new[] { "direct", "auto", "finite", "auto-wait", "finite-wait", "poll", "external" })
                await PostedAdmission(mode, diagnostics, check);
        }
        await CallbackFailure(check);
        await StandaloneBatch(check);
        await EqualHeaders(check);
        await QuietEarlyVeto(check);
        foreach (string retirement in new[] { "hold", "reset", "close" }) await AdmissionLifecycle(retirement, check);
    }

    private static async Task Readback(string mode, bool diagnostics, Action<bool, string> check)
    {
        var initial = State(mode);
        var port = new Port(initial) { ReadError = new EngineCommandRejectedException("Readback unavailable") };
        var client = Session(port, diagnostics); client.Accept(initial);
        check(await Start(client, mode), "The " + mode + " readback control starts through its ordinary current owner; diagnostics=" + diagnostics);
        await Drain(client);
        var old = port.Commands.Single();
        check(client.State == initial && client.NeedsRecovery && client.UncertainCommand && client.LastFailureWasCommand
                && port.Reads == 1 && !client.Submit(old) && !client.Advance(_ => true, (_, _) => false)
                && !client.AdvanceOptionalPayments((_, _) => false) && port.Commands.Count == 1,
            "Post-dispatch " + mode + " read failure blocks stale controls and all continuations independently of exception type/logging");
        port.ReadError = null; port.Next = Fresh(initial);
        client.Poll(false, _ => { }); await Drain(client);
        check(client.State == port.Next && !client.NeedsRecovery && !client.UncertainCommand && !client.Submit(old)
                && port.Commands.Count == 1 && port.Reads == 2,
            "Ordinary observation reconciles " + mode + " once without replaying its returned command");
        var fresh = await CurrentCommand(client, mode);
        check(client.Submit(fresh), "Only a deliberately requested fresh " + mode + " command is admitted after recovery");
        await Drain(client);
        check(port.Commands.Count == 2 && port.Reads == 3 && !client.NeedsRecovery && !client.UncertainCommand,
            "Recovered " + mode + " sends exactly one old and one explicit fresh command");
        client.Close();
    }

    private static async Task FirstRejected(string mode, bool diagnostics, Action<bool, string> check)
    {
        var initial = State(mode); var rejection = new EngineCommandRejectedException("Current dispatch was rejected");
        var port = new Port(initial) { RejectAt = 1, Rejection = rejection };
        var client = Session(port, diagnostics); client.Accept(initial); Exception? reported = null;
        client.Failed += error => reported = error;
        check(await Start(client, mode), "The first " + mode + " dispatch rejection begins in the ordinary lane"); await Drain(client);
        check(ReferenceEquals(reported, rejection) && !client.NeedsRecovery && !client.UncertainCommand && client.LastFailureWasCommand
                && client.State == initial && port.Commands.Count == 1 && port.Reads == 0,
            "A definite first " + mode + " Execute rejection preserves its original cause and never invents an uncertain application");
        client.Close();
    }

    private static async Task WaitWithoutCommand(string mode, bool diagnostics, Action<bool, string> check)
    {
        var armed = State(mode); var waiting = armed with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1 };
        var port = new Port(waiting) { ReadError = new EngineCommandRejectedException("Observation unavailable") };
        var client = Session(port, diagnostics); client.Accept(waiting);
        bool started = mode == "finite" ? client.AdvanceFinite(FiniteResolutionRegression.Arm(armed)!, (_, _) => false)
            : client.Advance(_ => true, (_, _) => false);
        check(started, "A " + mode + " continuation can wait without dispatching a command"); await Drain(client);
        check(client.State == waiting && client.NeedsRecovery && !client.UncertainCommand && !client.LastFailureWasCommand
                && port.Commands.Count == 0 && port.Reads == 1,
            "An observation-only " + mode + " failure requires recovery without inventing a command or application uncertainty");
        port.ReadError = null; port.Next = Fresh(armed); client.Poll(false, _ => { }); await Drain(client);
        check(!client.NeedsRecovery && !client.UncertainCommand && port.Commands.Count == 0,
            "An observation-only failure reconciles through a read with zero command replay");
        client.Close();
    }

    private static async Task PartiallyCommittedBatch(string mode, bool diagnostics, Action<bool, string> check)
    {
        var initial = State(mode);
        // Another priority round is legal only after real queue progress; fresh
        // handles alone intentionally stop this existing stack continuation.
        var next = mode == "stack" ? FiniteResolutionRegression.State(2, completed: 1, revision: 2) with {
            StackInterruptionVersion = initial.StackInterruptionVersion
        } : Fresh(initial);
        var port = new Port(next) { RejectAt = 2 };
        var client = Session(port, diagnostics); client.Accept(initial);
        check(await Start(client, mode), "A two-command " + mode + " batch starts through the current operation owner"); await Drain(client);
        check(port.Commands.Count == 2 && port.Reads == 1 && client.State == port.Next && !client.NeedsRecovery && !client.UncertainCommand
                && !client.Submit(port.Commands[0]) && port.Commands.Count == 2,
            "The owner publishes the earlier applied " + mode + " command before a later definite dispatch rejection can retain safe controls");
        port.RejectAt = 0; client.Poll(false, _ => { }); await Drain(client);
        check(client.State == port.Next && !client.NeedsRecovery && !client.UncertainCommand && port.Commands.Count == 2,
            "A partially applied " + mode + " batch reconciles its actual current state without replaying either dispatch");
        client.Close();
    }

    private static async Task PostedAdmission(string mode, bool diagnostics, Action<bool, string> check)
    {
        var armed = FiniteResolutionRegression.State(2, completed: 1);
        armed = armed with { Decision = armed.Decision! with { Payload = ((PriorityPayload)armed.Decision.Payload) with { CanAutoPass = true } } };
        bool waiting = mode.EndsWith("-wait", StringComparison.Ordinal);
        bool submitted = mode is "direct" or "auto" or "finite";
        var initial = waiting ? armed with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1 } : armed;
        // Both frames are valid individually. Only the already accepted cursor
        // can reject this same-audience execution-history rollback.
        var invalid = FiniteResolutionRegression.State(2, revision: 2);
        ExecutionFrames.Validate(invalid);
        var port = new Port(invalid);
        var client = new ClientSession(port, observationDelivery: mode == "external" ? ObservationDeliveryMode.External : ObservationDeliveryMode.Polled);
        if (diagnostics) client.DiagnosticRecorded += _ => { };
        client.Accept(initial);
        bool directRejected = false;
        try { client.Accept(invalid); } catch (ArgumentException) { directRejected = true; }
        check(directRejected && client.State == initial && !client.NeedsRecovery,
            "Public direct acceptance still exposes a cross-frame contract violation to its caller");
        int failures = 0, publications = 0;
        client.Failed += _ => failures++; client.Changed += (_, _) => publications++;
        var expected = initial;
        bool held = mode is not ("external" or "poll");
        if (held) {
            port.Next = initial with { Revision = 3, BoardRevision = 3 };
            port.HoldRead = true;
        }
        if (mode == "external") {
            using var ingress = client.OpenObservationIngress(initial.Id, initial.ViewerId);
            check(ingress.TryPost(invalid), "An external provider can queue a bounded candidate for owner-thread validation");
            client.Pump();
        } else if (mode == "poll") client.Poll(false, _ => throw new InvalidOperationException("Invalid poll cannot invoke its completion"));
        else if (mode.StartsWith("finite", StringComparison.Ordinal))
            check(client.AdvanceFinite(FiniteResolutionRegression.Arm(armed)!, (before, after) => after.Revision > before.Revision), "A finite admission probe starts in its ordinary lane");
        else if (mode.StartsWith("auto", StringComparison.Ordinal))
            check(client.Advance(_ => true, (before, after) => after.Revision > before.Revision), "An automatic admission probe starts in its ordinary lane");
        else check(client.Submit(new PassPriority(initial.Id, initial.Decision!.Id)), "A direct admission probe starts in its ordinary lane");
        if (held) {
            await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            expected = FiniteResolutionRegression.State(2, completed: 2, revision: 2);
            check(client.Accept(expected), "A newer same-audience terminal cursor is accepted while the provider read is held");
            publications = 0; port.ReadRelease.TrySetResult();
        }
        await Drain(client);
        check(client.State == expected && client.NeedsRecovery && client.UncertainCommand == submitted
                && client.LastFailureWasCommand == submitted && failures == 1 && publications == 0
                && port.Commands.Count == (submitted ? 1 : 0),
            "Provider-owned " + mode + " semantic admission fails closed before commit without escaping the pump or inventing a submitted command");
        client.Close();
    }

    private static async Task BeforeContinuation(string mode, bool diagnostics, Action<bool, string> check)
    {
        var initial = State(mode) with { Executions = FiniteResolutionRegression.State(2, completed: 2).Executions };
        var invalid = Fresh(initial) with { Executions = FiniteResolutionRegression.State(2).Executions };
        ExecutionFrames.Validate(initial); ExecutionFrames.Validate(invalid);
        var port = new Port(invalid) { RejectAt = 2 }; var client = Session(port, diagnostics); client.Accept(initial);
        check(await Start(client, mode), "A changed-prompt " + mode + " rollback adversary starts from a valid retained execution cursor");
        await Drain(client);
        check(port.Commands.Count == 1 && port.Reads == 1 && client.State == initial && client.NeedsRecovery && client.UncertainCommand,
            "A per-frame-valid execution-history rollback cannot authorize a second " + mode + " command before owner-pump admission");
        client.Close();
    }

    private static async Task NewOwnerBeforeContinuation(string mode, bool diagnostics, Action<bool, string> check)
    {
        var initial = State(mode) with { Executions = FiniteResolutionRegression.State(2, completed: 2).Executions };
        var port = new Port(Fresh(initial) with { Revision = 3, BoardRevision = 3 }) { HoldRead = true, RejectAt = 2 };
        var client = Session(port, diagnostics); client.Accept(initial);
        int failed = 0, publications = 0; client.Failed += _ => failed++;
        client.Changed += (_, _) => publications++;
        check(await Start(client, mode), "A held changed-prompt " + mode + " read is individually valid against its worker prefix");
        await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = FiniteResolutionRegression.State(3, completed: 3, revision: 2);
        check(client.Accept(newer), "A newer owner cursor is authoritative while the earlier provider response waits");
        publications = 0; port.ReadRelease.TrySetResult(); await Drain(client);
        check(port.Commands.Count == 1 && port.Reads == 1 && client.State == newer && failed == 1 && publications == 0
                && client.NeedsRecovery && client.UncertainCommand && !client.Submit(port.Commands[0]),
            "Current-owner admission prevents a second " + mode + " command and rejects the stale read once without republishing it or clearing recovery");
        port.Next = newer with { Revision = 4, BoardRevision = 4 };
        client.Poll(false, _ => { }); await Drain(client);
        check(client.State == port.Next && !client.NeedsRecovery && !client.UncertainCommand && port.Commands.Count == 1,
            "Only an ordinary fresh observation recovers a newer-owner admission rejection without replay");
        client.Close();
    }

    private static async Task StandaloneBatch(Action<bool, string> check)
    {
        var initial = State("auto"); var port = new Port(Fresh(initial)) { RejectAt = 2 };
        Exception? failure = null;
        try { await new DecisionAdvance(port).RunAsync(initial, _ => true, (_, _) => false); }
        catch (IOException error) { failure = error; }
        check(failure is IOException && ReferenceEquals(failure.InnerException, port.Rejection)
                && port.Commands.Count == 2 && port.Reads == 1,
            "A standalone batch without an owner publication ACK retains earlier unpublished dispatch uncertainty rather than exposing a bare final rejection");
    }

    private static async Task EqualHeaders(Action<bool, string> check)
    {
        var initial = State("auto");
        var next = initial with { Decision = initial.Decision! with {
            Payload = ((PriorityPayload)initial.Decision.Payload) with { CanAutoPass = false } } };
        check(initial == initial with { } && initial != next && initial.Revision == next.Revision && initial.Decision.Id == next.Decision!.Id,
            "Full immutable-record equality deduplicates an unchanged clone but distinguishes a changed same-header permission payload");
        var port = new Port(next); var client = new ClientSession(port); client.Accept(initial);
        int publications = 0; client.Changed += (_, _) => publications++;
        check(client.Advance(_ => true, (_, after) => after.Decision?.Payload is PriorityPayload { CanAutoPass: false }),
            "A distinct read can retain its header while changing its current response permission");
        await Drain(client);
        check(ReferenceEquals(client.State, next) && client.State.Decision!.Payload is PriorityPayload { CanAutoPass: false }
                && publications == 1 && port.Commands.Count == 1 && port.Reads == 1 && !client.NeedsRecovery,
            "Owner admission commits a distinct equal-header payload exactly once rather than acting on unaccepted data or suppressing it as a duplicate");
        client.Pump(); check(publications == 1, "The exact already-admitted read is never reposted by the final continuation");
        client.Close();
    }

    private static async Task QuietEarlyVeto(Action<bool, string> check)
    {
        var initial = State("quiet"); var next = Fresh(initial); var port = new Port(next);
        var client = new ClientSession(port); client.Accept(initial);
        check(client.AdvanceQuietly("quiet-token", _ => true, (before, after) => after.Revision > before.Revision),
            "A prior quiet operation establishes its own admitted read before the next operation");
        await Drain(client);
        var current = Fresh(next); client.Accept(current);
        int publications = 0, allowed = 0, commands = port.Commands.Count, reads = port.Reads;
        client.Changed += (_, _) => publications++;
        check(client.AdvanceQuietly("quiet-token", _ => Interlocked.Increment(ref allowed) == 1, (_, _) => false),
            "A new quiet policy can admit its owner entry and veto before its worker starts reading");
        await Drain(client);
        check(ReferenceEquals(client.State, current) && publications == 0 && port.Commands.Count == commands && port.Reads == reads
                && !client.NeedsRecovery && !client.UncertainCommand,
            "An early quiet veto cannot reuse a prior operation's publication or suppression state");
        client.Close();
    }

    private static async Task AdmissionLifecycle(string retirement, Action<bool, string> check)
    {
        var initial = retirement == "hold" ? State("auto") : State("finite") with {
            FiniteStackYield = new("", FiniteStackYieldStatus.Idle, [], [], 1, 1) };
        var port = new Port(retirement == "hold" ? Fresh(initial) : initial) { ServerYield = retirement != "hold" };
        var client = new ClientSession(port); client.Accept(initial);
        using var release = new ManualResetEventSlim();
        var readReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0, failures = 0, publications = 0;
        client.Failed += _ => failures++; client.Changed += (_, _) => publications++;
        client.DiagnosticRecorded += item => {
            if (item.Stage == ClientDiagnosticStage.ProviderObservationReturned && Interlocked.Exchange(ref seen, 1) == 0) {
                readReturned.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The controlled post-read gate was not released");
            }
        };
        check(retirement == "hold" ? client.Advance(_ => true, (_, _) => false)
            : client.AdvanceFinite(FiniteResolutionRegression.Arm(initial)!, (_, _) => false),
            "A current " + retirement + " control reaches its returned provider read before owner admission");
        try {
            await readReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (retirement == "hold") client.CancelAdvance();
            else if (retirement == "reset") client.Reset();
            else client.Close();
        } finally { release.Set(); }
        int retiredPublications = publications;
        if (retirement == "close") {
            // Deliberately no Pump: Close skips queued owner callbacks, so its
            // negative cleanup is the witness that the ACK wait was released.
            await port.RevokeObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(client.Closed && publications == retiredPublications && failures == 0
                    && port.Commands is [GrantFiniteStackYield first, RevokeFiniteStackYield last] && first.GrantId == last.GrantId,
                "Close releases an unpumped admission wait and serially withdraws only the already-submitted grant");
        } else {
            await Drain(client);
            check(!client.NeedsRecovery && !client.UncertainCommand && failures == 0
                    && (retirement == "hold" ? client.State == port.Next && publications == retiredPublications + 1 && port.Commands.Count == 1
                        : client.State == null && publications == retiredPublications
                            && port.Commands is [GrantFiniteStackYield first, RevokeFiniteStackYield last] && first.GrantId == last.GrantId),
                retirement == "hold" ? "Hold admits an already-sent result exactly once and stops without replay or gratuitous recovery"
                    : "Reset releases its retired admission wait, publishes no old scope and acknowledges exactly one serialized negative cleanup");
            client.Close();
        }
    }

    private static async Task CallbackFailure(Action<bool, string> check)
    {
        var initial = State("direct"); var next = Fresh(initial); var port = new Port(next);
        var client = new ClientSession(port); client.Accept(initial);
        var callbackError = new InvalidOperationException("Deliberate UI callback failure");
        client.Changed += (_, _) => throw callbackError;
        client.Submit(new PassPriority(initial.Id, initial.Decision!.Id)); Exception? reported = null;
        try { await Drain(client); } catch (InvalidOperationException error) { reported = error; }
        check(ReferenceEquals(reported, callbackError) && client.State == next && !client.NeedsRecovery && !client.UncertainCommand,
            "The narrow admission guard never disguises a post-commit UI callback failure as an invalid provider frame");
        await Drain(client); client.Close();
    }

    private static ClientSession Session(Port port, bool diagnostics)
    {
        var client = new ClientSession(port, sourceActions: port);
        if (diagnostics) client.DiagnosticRecorded += _ => { };
        return client;
    }

    private static GameSnapshot State(string mode)
    {
        if (mode is "finite" or "stack") return FiniteResolutionRegression.State(2);
        if (mode == "optional") {
            var state = FiniteResolutionRegression.State(2, paying: true);
            return state with { Decision = state.Decision! with { Payload = ((PaymentPayload)state.Decision.Payload) with {
                UnpaidManaParts = [new("{1}", 1)], OptionalPaymentSupport = new("fixed-one", [new("{1}", 1)],
                    OptionalManaPaymentAvailability.Unpayable, false)
            } } };
        }
        var empty = FiniteResolutionRegression.State(0);
        return empty with { Decision = new("priority", new(), new PriorityPayload(new("Pass", true), CanAutoPass: true,
            QuietYieldToken: "quiet-token")), DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority" };
    }

    private static GameSnapshot Fresh(GameSnapshot initial) => initial with {
        Revision = initial.Revision + 1, BoardRevision = initial.BoardRevision + 1,
        Decision = initial.Decision! with { Id = initial.Decision.Id + "-fresh" }
    };

    private static async Task<bool> Start(ClientSession client, string mode) => mode switch {
        "auto" => client.Advance(_ => true, (_, _) => false),
        "quiet" => client.AdvanceQuietly("quiet-token", _ => true, (_, _) => false),
        "finite" => client.AdvanceFinite(FiniteResolutionRegression.Arm(client.State!)!, (_, _) => false),
        "stack" => client.AdvanceStack(StackResolutionIntent.TryArm(client.State!)!, (_, _) => false),
        "optional" => client.AdvanceOptionalPayments((_, _) => false),
        _ => client.Submit(await CurrentCommand(client, mode))
    };

    private static async Task<GameCommand> CurrentCommand(ClientSession client, string mode)
    {
        var state = client.State!; var decision = state.Decision!;
        if (mode == "source") {
            var source = state.Viewer!.Zone("Battlefield").Cards.Single();
            var context = new SourceActionContext(state.Id, decision.Id, state.Revision, state.ViewerId, source.Key, CardViews.Identity(source));
            client.SourceActions.Observe(context);
            await Until(client, () => client.SourceActions.View.Status != SourceActionStatus.Loading);
            return client.SourceActions.TryCreateCommand(context, "activate") ?? throw new InvalidOperationException("Current source action was not offered");
        }
        return mode switch {
            "auto" => new AutoPassPriority(state.Id, decision.Id),
            "quiet" => new PassForNow(state.Id, decision.Id, "quiet-token"),
            "optional" => new CancelPayment(state.Id, decision.Id),
            _ => new PassPriority(state.Id, decision.Id)
        };
    }

    private static Task Drain(ClientSession client) => Until(client, () => !client.Busy && !client.Polling);
    private static async Task Until(ClientSession client, Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        do { client.Pump(); if (done()) return; await Task.Delay(1); } while (DateTime.UtcNow < end);
        throw new TimeoutException("The command-phase fixture did not settle its controlled response");
    }

    private sealed class Port(GameSnapshot next) : IMatchConnection, ISourceActions
    {
        internal GameSnapshot Next = next;
        internal List<GameCommand> Commands { get; } = [];
        internal int Reads, RejectAt;
        internal Exception? ReadError;
        internal bool HoldRead;
        internal bool ServerYield;
        internal TaskCompletionSource RevokeObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal EngineCommandRejectedException Rejection = new("Current dispatch was rejected");
        public bool SupportsSourceActions => true;
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Add(command);
            if (ServerYield && command is GrantFiniteStackYield grant) Next = Next with { Revision = Next.Revision + 1,
                FiniteStackYield = new(grant.GrantId, FiniteStackYieldStatus.Active, grant.ExecutionIds, grant.ExecutionIds, 1, 1) };
            if (ServerYield && command is RevokeFiniteStackYield) Next = Next with { Revision = Next.Revision + 1,
                FiniteStackYield = Next.FiniteStackYield! with { Status = FiniteStackYieldStatus.Revoked } };
            return RejectAt == Commands.Count ? Task.FromException(Rejection) : Task.CompletedTask;
        }
        public async Task<GameSnapshot?> ObserveAsync()
        {
            Reads++;
            var captured = Next; ReadStarted.TrySetResult();
            if (HoldRead && Reads == 1) await ReadRelease.Task;
            if (ReadError != null) throw ReadError;
            if (captured.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked) RevokeObserved.TrySetResult();
            return captured;
        }
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
            => Task.FromResult(new SourceActionSet(gameId, decisionId, sourceCardKey, "source", [new("activate", "activate", "Activate current source")]) {
                Revision = Next.Revision });
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
