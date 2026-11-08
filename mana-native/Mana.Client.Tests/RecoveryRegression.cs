using Mana.Client;
using Mana.Contracts;

internal static class RecoveryRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await SupersededObservations(check);
        await SameCauseDistinctPhases(check);
        await CapturedFailureContext(check);
        await RejectedThenStalledObservation(check);
        var state = new GameSnapshot("recovery", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null,
            new Decision("before", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: true, QuietYieldToken: "", Abilities: []))) { Revision = 1 };
        var connection = new FailingConnection(state);
        var client = new ClientSession(connection); client.Accept(state);
        var synchronized = client.LastSynchronizedAt;
        connection.FailReads = true;
        client.Poll(false, _ => { }); await Drain(client);
        check(client.NeedsRecovery && !client.UncertainCommand && client.LastSynchronizedAt == synchronized && client.State == state,
            "Failed observation retains inspectable state and its synchronization timestamp, marked stale");
        var reply = new PassPriority(state.Id, "before");
        check(!client.Submit(reply) && !client.Advance(_ => true, (_, _) => false) && connection.Replies == 0,
            "Stale decisions cannot be submitted manually or automatically");
        connection.FailReads = false;
        client.Poll(false, _ => { }); await Drain(client);
        check(!client.NeedsRecovery && client.LastConnectionError.Length == 0 && client.LastSynchronizedAt >= synchronized,
            "A fresh authoritative observation restores play and clears the connection warning");
        connection.FailReply = true;
        check(client.Submit(reply), "A recovered connection accepts the current scoped action"); await Drain(client);
        check(client.NeedsRecovery && client.UncertainCommand && connection.Replies == 1,
            "A failed command acknowledgement is explicitly uncertain and is never automatically resubmitted");
        connection.State = state with { Revision = 2, Decision = state.Decision! with { Id = "after" } };
        connection.FailReply = false;
        client.Poll(false, _ => { }); await Drain(client);
        check(!client.NeedsRecovery && !client.Submit(reply) && client.State?.Decision?.Id == "after" && connection.Replies == 1,
            "Recovery reconciles the next decision without replaying an uncertain old command");
        client.Reset();
        check(client.State == null && !client.NeedsRecovery, "Leaving retires the stale table and recovery state together");

        var rejectedConnection = new FailingConnection(state) { RejectReply = true };
        var rejected = new ClientSession(rejectedConnection); rejected.Accept(state);
        ClientOperationFailure? reported = null;
        rejected.Failed += error => reported = error;
        check(rejected.Submit(reply), "A definite rejection begins as one ordinary scoped command"); await Drain(rejected);
        check(reported is { Phase: ClientFailurePhase.DispatchRejected, Cause: EngineCommandRejectedException }
                && reported.Message == "That choice has changed. Use the current prompt."
                && rejected.LastFailureWasCommand && !rejected.NeedsRecovery && !rejected.UncertainCommand && rejected.LastConnectionError.Length == 0,
            "An explicit engine rejection retains the action error without pretending the connection or acknowledgement was lost");
        rejectedConnection.State = state with { Revision = 2, Decision = state.Decision! with { Id = "after-rejection" } };
        rejected.Poll(false, _ => { }); await Drain(rejected);
        check(rejected.State?.Decision?.Id == "after-rejection" && rejected.LastFailureWasCommand
                && reported is { Phase: ClientFailurePhase.DispatchRejected, Cause: EngineCommandRejectedException }
                && rejectedConnection.Replies == 1 && !rejected.Submit(reply),
            "Fresh observation reconciles the rejected decision without replaying it or erasing the command-failure acknowledgement");
        rejectedConnection.FailReads = true;
        rejected.Poll(false, _ => { }); await Drain(rejected);
        check(rejected.NeedsRecovery && !rejected.LastFailureWasCommand && !rejected.UncertainCommand
                && reported is { Phase: ClientFailurePhase.Observation, Cause: IOException },
            "A later observation failure is classified as a connection failure rather than a rejected command");
        rejected.Reset();
        check(!rejected.LastFailureWasCommand && !rejected.NeedsRecovery, "Leaving clears rejection classification with the retired table");

        var clock = new RecoveryClock(); var stalled = new StalledConnection();
        var waiting = new ClientSession(stalled, clock: clock); waiting.Accept(state);
        waiting.Poll(false, _ => { });
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now += TimeSpan.FromSeconds(6);
        check(waiting.NeedsRecovery && !waiting.Submit(reply) && !waiting.Advance(_ => true, (_, _) => false),
            "A stalled observation marks the board stale and blocks play before the connection timeout");
        stalled.Snapshot.SetResult(state with { Revision = 2 }); await Drain(waiting);
        check(!waiting.NeedsRecovery && waiting.LastSynchronizedAt == clock.Now,
            "A late healthy observation restores freshness without dropping the retained table");

        var ignored = new PriorityAbility("fetch", "card", "Arid Mesa", "Search", true);
        var queuedConnection = new FailingConnection(state with { IgnoredPriorityAbilities = [ignored] }) { FailReply = true };
        var queued = new ClientSession(queuedConnection); queued.Accept(queuedConnection.State);
        queued.Submit(reply);
        check(queued.SubmitSetting(new RestorePriorityAbility(state.Id, AbilityId: ignored.Id)),
            "Undo can queue behind an in-flight command before its acknowledgement fails");
        await Drain(queued);
        check(queued.NeedsRecovery && queuedConnection.Replies == 1, "Queued Undo waits for recovery rather than being lost or sent on stale state");
        queuedConnection.FailReply = false;
        queued.Poll(false, _ => { }); await Drain(queued);
        check(queuedConnection.Replies == 2 && queued.State!.IgnoredPriorityAbilities.Length == 0,
            "A queued Undo executes exactly once after fresh reconciliation");
    }
    private static async Task SupersededObservations(Action<bool, string> check)
    {
        var initial = new GameSnapshot("superseded-read", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null,
            new Decision("before", new(), new PriorityPayload(new("Pass", true)))) { Revision = 1 };
        var fresh = initial with { Revision = 2, Decision = initial.Decision! with { Id = "fresh" } };
        var port = new HeldReadConnection(); var clock = new RecoveryClock();
        var session = new ClientSession(port, clock: clock); session.Accept(initial);
        int failures = 0, publications = 0, callbacks = 0;
        session.Failed += _ => failures++;
        session.Changed += (_, _) => publications++;
        var synchronized = session.LastSynchronizedAt;
        session.Poll(false, _ => callbacks++);
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        port.Read.SetException(new EngineObservationSupersededException()); await Drain(session);
        check(session.State == initial && !session.NeedsRecovery && !session.UncertainCommand
                && session.LastConnectionError.Length == 0 && session.LastSynchronizedAt == synchronized
                && failures == 0 && publications == 0 && callbacks == 0 && port.Reads == 1 && port.Commands == 0
                && GameCommandPermissions.Allows(session.State!, new PassPriority(initial.Id, "before")),
            "A superseded ordinary read publishes no failure, snapshot or command and preserves the current replica until fresh observation");
        port.Arm(); clock.Now += TimeSpan.FromMilliseconds(1);
        session.Poll(false, _ => callbacks++);
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        port.Read.SetResult(fresh); await Drain(session);
        check(session.State == fresh && !session.NeedsRecovery && failures == 0 && publications == 1 && callbacks == 1
                && session.LastSynchronizedAt == clock.Now && port.Reads == 2 && port.Commands == 0,
            "The next ordinary poll accepts one fresh current frame after safe supersession without replaying an action");
        session.Close();

        foreach (string lifecycle in new[] { "viewer-roundtrip", "reset" }) {
            var held = new HeldReadConnection(); var owner = new ClientSession(held, clock: clock); owner.Accept(initial);
            int lateFailures = 0, latePublications = 0, lateCallbacks = 0;
            owner.Failed += _ => lateFailures++;
            owner.Poll(false, _ => lateCallbacks++);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (lifecycle == "viewer-roundtrip") {
                check(owner.Accept(initial with { ViewerId = 1, Revision = 3 }) && owner.Accept(initial with { Revision = 4 }),
                    "Explicit audience ABA retires the held ordinary read before its typed supersession completes");
            } else owner.Reset();
            var current = owner.State; var acceptedAt = owner.LastSynchronizedAt;
            owner.Changed += (_, _) => latePublications++;
            held.Read.SetException(new EngineObservationSupersededException()); await Drain(owner);
            check(owner.State == current && owner.LastSynchronizedAt == acceptedAt && !owner.NeedsRecovery
                    && !owner.UncertainCommand && owner.LastConnectionError.Length == 0 && lateFailures == 0
                    && latePublications == 0 && lateCallbacks == 0 && held.Reads == 1 && held.Commands == 0,
                "A retired " + lifecycle + " ordinary read cannot revive state, errors, callbacks or command authority after supersession");
            owner.Close();
        }

        foreach (bool duringExecution in new[] { true, false }) {
            var uncertainPort = new HeldReadConnection { ExecuteError = duringExecution ? new EngineObservationSupersededException() : null };
            var uncertain = new ClientSession(uncertainPort, clock: clock); uncertain.Accept(initial);
            ClientOperationFailure? reported = null; uncertain.Failed += error => reported = error;
            check(uncertain.Submit(new PassPriority(initial.Id, "before")),
                "A current command starts before testing supersession during " + (duringExecution ? "execution" : "command readback"));
            if (!duringExecution) {
                await uncertainPort.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                uncertainPort.Read.SetException(new EngineObservationSupersededException());
            }
            await Drain(uncertain);
            check(reported is { Cause: EngineObservationSupersededException }
                    && reported.Phase == (duringExecution ? ClientFailurePhase.DispatchUnknown : ClientFailurePhase.ReadbackUnknown)
                    && uncertain.NeedsRecovery && uncertain.UncertainCommand
                    && uncertain.LastFailureWasCommand && uncertain.State == initial && uncertainPort.Commands == 1
                    && uncertainPort.Reads == (duringExecution ? 0 : 1)
                    && !uncertain.Submit(new PassPriority(initial.Id, "before")),
                "The observation-only signal during " + (duringExecution ? "execution" : "command readback")
                    + " remains an uncertain command failure and cannot authorize a retry");
            uncertainPort.Arm(); uncertain.Poll(false, _ => { });
            await uncertainPort.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            uncertainPort.Read.SetResult(fresh); await Drain(uncertain);
            check(uncertain.State == fresh && !uncertain.NeedsRecovery && !uncertain.UncertainCommand && uncertainPort.Commands == 1,
                "Fresh ordinary reconciliation clears command uncertainty without resubmitting the possibly accepted action");
            uncertain.Close();
        }
    }
    private static async Task SameCauseDistinctPhases(Action<bool, string> check)
    {
        // An engine rejection-shaped exception is not a dispatch rejection when
        // it came from observation. All three operations use the SAME cause.
        var cause = new EngineCommandRejectedException("Same provider failure, different operation phase.");
        foreach (string stage in new[] { "observation", "dispatch", "readback" }) {
            var initial = FailureState();
            var port = new HeldReadConnection { ExecuteError = stage == "dispatch" ? cause : null };
            var session = new ClientSession(port); session.Accept(initial);
            var failures = new List<ClientOperationFailure>(); session.Failed += failures.Add;
            if (stage == "observation") session.Poll(false, _ => { });
            else check(session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
                "The phase-preservation " + stage + " fixture submits its one current command");
            if (stage != "dispatch") {
                await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                port.Read.SetException(cause);
            }
            await Drain(session);
            var failure = failures.Single();
            var phase = stage == "dispatch" ? ClientFailurePhase.DispatchRejected
                : stage == "readback" ? ClientFailurePhase.ReadbackUnknown : ClientFailurePhase.Observation;
            check(failure.Phase == phase && ReferenceEquals(failure.Cause, cause)
                    && failure.Context is { GameId: "failure-context", ViewerId: 0, Revision: 11, BoardRevision: 17, DecisionId: "before" }
                    && failure.Context.Epoch == (stage == "observation" ? 0 : 1),
                "The same provider cause retains its actual " + stage + " phase and admitted game/viewer/epoch/decision context");
            check(session.RecoveryState == (stage == "dispatch" ? ClientRecoveryState.Ready
                    : stage == "readback" ? ClientRecoveryState.CommandUncertain : ClientRecoveryState.ReconciliationRequired)
                    && session.NeedsRecovery == (stage != "dispatch")
                    && session.UncertainCommand == (stage == "readback")
                    && session.LastFailureWasCommand == (stage != "observation")
                    && ReferenceEquals(session.LastFailure, failure)
                    && (stage == "dispatch" ? session.RecoveryOrigin == null : ReferenceEquals(session.RecoveryOrigin, failure)),
                "Session-owned recovery state derives consistent public health facts for " + stage + " without reclassifying the cause");

            ClientOperationFailure latest = failure;
            if (stage == "readback") {
                check(!session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)) && port.Commands == 1,
                    "Uncertain readback cannot submit the already-dispatched command again");
                port.Arm(); session.Poll(false, _ => { });
                await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var subsequent = new IOException("Reconciliation read also failed.");
                port.Read.SetException(subsequent); await Drain(session);
                latest = failures.Last();
                check(failures.Count == 2 && latest.Phase == ClientFailurePhase.Observation
                        && ReferenceEquals(latest.Cause, subsequent) && ReferenceEquals(session.LastFailure, latest)
                        && ReferenceEquals(session.RecoveryOrigin, failure)
                        && session.RecoveryState == ClientRecoveryState.CommandUncertain && session.UncertainCommand
                        && !session.LastFailureWasCommand && port.Commands == 1,
                    "A later failed reconciliation retains the first uncertain operation while reporting its own observation phase");
            }

            port.Arm(); session.Poll(false, _ => { });
            await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var fresh = initial with { Revision = 12, BoardRevision = 18, Decision = initial.Decision! with { Id = "after" } };
            port.Read.SetResult(fresh); await Drain(session);
            int commands = stage == "observation" ? 0 : 1;
            check(session.State == fresh && session.RecoveryState == ClientRecoveryState.Ready
                    && !session.NeedsRecovery && !session.UncertainCommand && session.RecoveryOrigin == null
                    && ReferenceEquals(session.LastFailure, latest) && port.Commands == commands,
                "Fresh authoritative reconciliation clears the active " + stage + " recovery episode without replay or losing the failure fact");
            session.Reset();
            check(session.LastFailure == null && session.RecoveryOrigin == null && session.RecoveryState == ClientRecoveryState.Ready,
                "Reset retires the " + stage + " failure fact and its recovery ownership together");
            session.Close();
        }
    }

    private static async Task CapturedFailureContext(Action<bool, string> check)
    {
        var initial = FailureState(); var port = new HeldReadConnection();
        var session = new ClientSession(port); session.Accept(initial);
        check(session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
            "A held command readback captures its operation context before later same-audience state arrives");
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = initial with { Revision = 12, BoardRevision = 18, Decision = initial.Decision! with { Id = "newer-before-failure" } };
        check(session.Accept(newer), "The context fixture admits a newer same-audience frame while the older readback is held");
        var cause = new EngineObservationSupersededException();
        port.Read.SetException(cause); await Drain(session);
        check(session.State == newer && session.LastFailure is { Phase: ClientFailurePhase.ReadbackUnknown } failure
                && ReferenceEquals(failure.Cause, cause)
                && failure.Context is { Epoch: 1, GameId: "failure-context", ViewerId: 0, Revision: 11, BoardRevision: 17, DecisionId: "before" }
                && session.UncertainCommand && port.Commands == 1,
            "A delayed readback failure reports its admitted operation rather than inventing context from the newer replica");
        session.Close();

        foreach (bool reset in new[] { false, true }) {
            var retiredPort = new HeldReadConnection(); var owner = new ClientSession(retiredPort); owner.Accept(initial);
            int failures = 0; owner.Failed += _ => failures++;
            check(owner.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
                "The late-failure fixture dispatches once before retiring its exact operation epoch");
            await retiredPort.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (reset) owner.Reset();
            else {
                check(owner.Accept(initial with { ViewerId = 1, Revision = 12 })
                        && owner.Accept(initial with { Revision = 13 }),
                    "An explicit viewer round trip retires the accepted command's earlier operation scope");
            }
            var current = owner.State;
            retiredPort.Read.SetException(new EngineObservationSupersededException()); await Drain(owner);
            check(owner.State == current && failures == 0 && owner.LastFailure == null && owner.RecoveryOrigin == null
                    && !owner.NeedsRecovery && !owner.UncertainCommand && retiredPort.Commands == 1,
                "A late failure after " + (reset ? "reset" : "viewer ABA") + " cannot revive a failure record, recovery state or old command");
            owner.Close();
        }
    }

    private static GameSnapshot FailureState() => new("failure-context", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null,
        new("before", new(), new PriorityPayload(new("Pass", true)))) { Revision = 11, BoardRevision = 17 };

    private static async Task RejectedThenStalledObservation(Action<bool, string> check)
    {
        var initial = FailureState(); var clock = new RecoveryClock();
        var rejection = new EngineCommandRejectedException("An earlier deliberate command was rejected.");
        var port = new HeldReadConnection { ExecuteError = rejection };
        var session = new ClientSession(port, clock: clock); session.Accept(initial);
        check(session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
            "The stalled-observation fixture first dispatches a deliberate command which the provider rejects");
        await Drain(session);
        var previous = session.LastFailure;
        check(previous is { Phase: ClientFailurePhase.DispatchRejected } && ReferenceEquals(previous.Cause, rejection)
                && session.RecoveryState == ClientRecoveryState.Ready && session.LastConnectionError.Length == 0 && port.Commands == 1,
            "A prior definite rejection remains a diagnostic fact without creating connection recovery");
        session.Poll(false, _ => { }); await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now += TimeSpan.FromSeconds(6);
        check(session.NeedsRecovery && session.RecoveryState == ClientRecoveryState.ReconciliationRequired
                && !session.UncertainCommand && session.RecoveryOrigin == null && ReferenceEquals(session.LastFailure, previous)
                && session.LastConnectionError.Length == 0 && port.Commands == 1 && port.Reads == 1,
            "A stalled observation cannot borrow an earlier command rejection as its connection-error cause");
        var fresh = initial with { Revision = 12, BoardRevision = 18, Decision = initial.Decision! with { Id = "after-stall" } };
        port.Read.SetResult(fresh); await Drain(session);
        check(session.State == fresh && session.RecoveryState == ClientRecoveryState.Ready && !session.NeedsRecovery
                && ReferenceEquals(session.LastFailure, previous) && session.LastConnectionError.Length == 0 && port.Commands == 1,
            "Fresh observation clears timer-only staleness without rewriting the earlier rejection or replaying its command");
        session.Close();
    }
    private static async Task Drain(ClientSession client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((client.Busy || client.Polling) && DateTime.UtcNow < deadline) { client.Pump(); await Task.Delay(5); }
        client.Pump();
        if (client.Busy || client.Polling) throw new TimeoutException("Recovery test did not finish");
    }
    private sealed class FailingConnection(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State = state;
        internal bool FailReads, FailReply, RejectReply;
        internal int Replies;
        public Task<GameSnapshot?> ObserveAsync() => FailReads ? Task.FromException<GameSnapshot?>(new IOException("Connection interrupted")) : Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand reply) {
            Replies++;
            if (FailReply) return Task.FromException(new IOException("Acknowledgement lost"));
            if (RejectReply) return Task.FromException(new EngineCommandRejectedException("That choice has changed. Use the current prompt."));
            if (reply is RestorePriorityAbility) State = State with { Revision = State.Revision + 1, IgnoredPriorityAbilities = [] };
            return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class RecoveryClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class HeldReadConnection : IMatchConnection
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? ExecuteError;
        internal int Reads, Commands;
        internal void Arm() {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public Task<GameSnapshot?> ObserveAsync() { Reads++; Started.TrySetResult(); return Read.Task; }
        public Task ExecuteAsync(GameCommand reply) {
            Commands++;
            return ExecuteError == null ? Task.CompletedTask : Task.FromException(ExecuteError);
        }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class StalledConnection : IMatchConnection
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> Snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GameSnapshot?> ObserveAsync() { Started.TrySetResult(); return Snapshot.Task; }
        public Task ExecuteAsync(GameCommand reply) => throw new InvalidOperationException("Stale state must not submit a command");
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}

// Keep this first consumer-boundary probe independent of the new payload's CLR
// type so the previous client assembly fails the behavioral phase assertion,
// rather than failing to load a new event delegate or record type.
internal static class FailureNotificationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var initial = new GameSnapshot("failure-notification", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null,
            new("before", new(), new PriorityPayload(new("Pass", true)))) { Revision = 1, BoardRevision = 1 };
        var port = new Port(); var session = new ClientSession(port); session.Accept(initial);
        object? reported = null; int failures = 0;
        Action<object> capture = value => { reported = value; failures++; };
        var notification = typeof(ClientSession).GetEvent("Failed")!;
        var handlerType = notification.EventHandlerType!;
        var parameter = System.Linq.Expressions.Expression.Parameter(handlerType.GetMethod("Invoke")!.GetParameters().Single().ParameterType);
        var body = System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(capture),
            System.Linq.Expressions.Expression.Convert(parameter, typeof(object)));
        var handler = System.Linq.Expressions.Expression.Lambda(handlerType, body, parameter).Compile();
        notification.AddEventHandler(session, handler);
        try {
            check(session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
                "The failure-boundary probe submits one currently offered command before holding its readback");
            await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cause = new EngineObservationSupersededException();
            port.Read.SetException(cause);
            await Drain(session);
            string? phase = reported?.GetType().GetProperty("Phase")?.GetValue(reported)?.ToString();
            object? original = reported?.GetType().GetProperty("Cause")?.GetValue(reported);
            check(phase == "ReadbackUnknown" && ReferenceEquals(original, cause) && failures == 1,
                "An accepted command readback failure crosses the session boundary with its phase and original cause");
            check(session.NeedsRecovery && session.UncertainCommand && !session.Submit(new PassPriority(initial.Id, initial.Decision.Id))
                    && port.Commands == 1 && port.Reads == 1,
                "The typed readback failure preserves uncertainty and cannot replay its already-dispatched command");
            var fresh = initial with { Revision = 2, BoardRevision = 2, Decision = initial.Decision! with { Id = "after" } };
            port.Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Poll(false, _ => { }); port.Read.SetResult(fresh); await Drain(session);
            check(session.State == fresh && !session.NeedsRecovery && !session.UncertainCommand && failures == 1 && port.Commands == 1,
                "A fresh ordinary observation reconciles the typed failure without repeating an operation or reporting a new failure");
        } finally { notification.RemoveEventHandler(session, handler); session.Close(); }
    }

    private static async Task Drain(ClientSession session)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        do { session.Pump(); if (!session.Busy && !session.Polling) return; await Task.Delay(1); } while (DateTime.UtcNow < end);
        throw new TimeoutException("The failure-boundary probe did not settle its controlled operation.");
    }

    private sealed class Port : IMatchConnection
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads, Commands;
        public Task<GameSnapshot?> ObserveAsync() { Reads++; Started.TrySetResult(); return Read.Task; }
        public Task ExecuteAsync(GameCommand command) { Commands++; return Task.CompletedTask; }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
