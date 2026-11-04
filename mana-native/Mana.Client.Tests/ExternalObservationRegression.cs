using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class ExternalObservationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = State(1); var clock = new Clock(); var connection = new Connection(state);
        var session = new ClientSession(connection, clock: clock, observationDelivery: ObservationDeliveryMode.External);
        using var ingress = session.OpenObservationIngress(state.Id, state.ViewerId);
        var order = new List<string>(); var acceptedAt = new List<DateTimeOffset>();
        session.Changed += (_, current) => { order.Add("state:" + current?.Revision); acceptedAt.Add(clock.Now); };
        session.Post(() => order.Add("before"));
        check(ingress.TryPost(state) && ingress.TryPost(State(2)) && session.State == null && session.LastSynchronizedAt == null,
            "External observations are queued evidence until the owning update pumps them, not an immediate alternate state pipeline");
        session.Post(() => order.Add("after"));
        bool lobbyCallback = false;
        session.Poll(false, _ => lobbyCallback = true); session.Poll(true, _ => lobbyCallback = true);
        check(!session.Polling && connection.Reads == 0 && !lobbyCallback,
            "External delivery starts no background match or lobby poll and creates no competing observation completion");
        clock.Now += TimeSpan.FromSeconds(2); session.Pump();
        check(order.SequenceEqual(["before", "state:1", "state:2", "after"]) && session.State?.Revision == 2
                && acceptedAt.All(at => at == clock.Now) && session.LastSynchronizedAt == clock.Now,
            "The existing completion pump preserves accepted observation order among ordinary callbacks and records one update's actual acceptance time");
        check(!ingress.TryPost(state with { Id = "another-game" }) && !ingress.TryPost(state with { ViewerId = 1 }),
            "An observation ingress cannot post another game's state or another viewer's private projection");

        // A queued read belongs to the epoch in which it was posted. Normal
        // command acceptance and authoritative receipts retain their own fence.
        check(ingress.TryPost(State(999)), "An external source can queue an observation before a command starts");
        connection.State = State(3);
        check(session.Submit(new PassPriority(state.Id, session.State!.Decision!.Id)),
            "External observation delivery does not invent or remove the connection's ordinary scoped command authority");
        await Drain(session);
        check(session.State?.Revision == 3 && connection.Commands == 1 && connection.Reads == 1
                && !order.Contains("state:999") && !session.NeedsRecovery,
            "A pre-command queued observation cannot replace the command's authoritative receipt, and that receipt retains the normal recovery path");
        check(ingress.TryPost(State(4)), "The same live source can post a later observation under the new command epoch");
        session.Pump();
        check(session.State?.Revision == 4, "A later external observation reconciles through the same replica after command completion");
        check(ingress.TryPost(State(6)) && ingress.TryPost(State(5)), "External delivery queues provider order without fabricating a second board reducer");
        session.Pump();
        check(session.State?.Revision == 6 && !order.Contains("state:5"),
            "The existing replica still rejects an out-of-order stale publication delivered through external ingress");

        PumpBoundary(check);
        CapacityAndRevocation(check);
        LifetimeAndScope(check);
        await AudienceCommandLifetime(check);
        await ComposedAudienceLifetimes(check);
        await BootstrapObservationAdmission(check);
        await TransportAudience(check);
        await ConcurrentProducer(check);
        check(Rejected(() => _ = new ClientSession(connection, observationDelivery: (ObservationDeliveryMode)99))
                && Rejected(() => _ = new ClientSession(connection).OpenObservationIngress(state.Id, state.ViewerId)),
            "Unknown delivery modes and external ingress on an ordinary polled session are rejected explicitly");
    }

    private static void PumpBoundary(Action<bool, string> check)
    {
        var state = State(1); var connection = new Connection(state);
        var session = new ClientSession(connection, observationDelivery: ObservationDeliveryMode.External);
        using var ingress = session.OpenObservationIngress(state.Id, state.ViewerId);
        var order = new List<string>();
        session.Changed += (_, current) => {
            order.Add("state:" + current?.Revision);
            if (current?.Revision != 1) return;
            bool posted = Task.Run(() => ingress.TryPost(State(2))).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (!posted) throw new InvalidOperationException("The next update's queued source observation was refused.");
            session.Post(() => order.Add("follow-up"));
        };
        ingress.TryPost(state); session.Pump();
        check(session.State?.Revision == 1 && order.SequenceEqual(["state:1"]),
            "A Changed callback cannot append an unbounded same-update external drain or make the next observation arrive early");
        session.Pump();
        check(session.State?.Revision == 2 && order.SequenceEqual(["state:1", "state:2", "follow-up"]),
            "The next update resumes the same FIFO completion queue without bypassing ordinary posted work");
    }

    private static void CapacityAndRevocation(Action<bool, string> check)
    {
        var state = State(1); var session = new ClientSession(new Connection(state), observationDelivery: ObservationDeliveryMode.External);
        using var old = session.OpenObservationIngress(state.Id, state.ViewerId);
        bool allPosted = true;
        for (int index = 1; index <= ClientSession.ObservationIngress.MaximumPendingObservations; index++)
            allPosted &= old.TryPost(State(index));
        check(allPosted && !old.TryPost(State(1000)), "External observation ingress enforces its finite pending bound before retaining another snapshot");
        old.Dispose();
        using var replacement = session.OpenObservationIngress(state.Id, state.ViewerId);
        check(!old.TryPost(state) && !replacement.TryPost(State(1000)),
            "Disposal and source replacement cannot bypass the session-wide queued bound or revive the retired producer");
        session.Pump();
        check(session.State == null && replacement.TryPost(State(1001)),
            "Revocation clears queued private payloads, and draining their finite tombstones restores capacity without publishing them");
        session.Pump();
        check(session.State?.Revision == 1001, "A fresh source observation becomes usable after bounded retired work has drained");

        var full = new ClientSession(new Connection(state), observationDelivery: ObservationDeliveryMode.External);
        using var due = full.OpenObservationIngress(state.Id, state.ViewerId);
        var accepted = new List<long>(); bool ordinary = false;
        full.Changed += (_, current) => accepted.Add(current!.Revision);
        full.Post(() => ordinary = true);
        for (int index = 1; index <= ClientSession.ObservationIngress.MaximumPendingObservations; index++) due.TryPost(State(index));
        full.Pump();
        check(ordinary && accepted.Count == ClientSession.ObservationIngress.MaximumPendingObservations
                && full.State?.Revision == ClientSession.ObservationIngress.MaximumPendingObservations,
            "Ordinary callbacks ahead of a full due observation group cannot split that group's actual acceptance across two updates");

        var interrupted = new ClientSession(new Connection(state), observationDelivery: ObservationDeliveryMode.External);
        using var retiring = interrupted.OpenObservationIngress(state.Id, state.ViewerId);
        var revisions = new List<long>();
        interrupted.Changed += (_, current) => { revisions.Add(current!.Revision); retiring.Dispose(); };
        retiring.TryPost(state); retiring.TryPost(State(2)); interrupted.Pump();
        check(revisions.SequenceEqual([1L]) && interrupted.State?.Revision == 1,
            "Revoking an ingress from the first accepted observation prevents the rest of that queued batch from outliving its owner");
    }

    private static void LifetimeAndScope(Action<bool, string> check)
    {
        var state = State(1); var connection = new Connection(state);
        var session = new ClientSession(connection, observationDelivery: ObservationDeliveryMode.External);
        var retired = session.OpenObservationIngress(state.Id, state.ViewerId);
        retired.TryPost(state);
        var current = session.OpenObservationIngress(state.Id, state.ViewerId);
        current.TryPost(State(2)); session.Pump();
        check(session.State?.Revision == 2 && !retired.TryPost(State(3)),
            "Opening a replacement ingress revokes the old source and prevents its queued publication from being accepted");
        check(Rejected(() => session.OpenObservationIngress("foreign-table", 0)) && current.TryPost(State(3)),
            "A failed foreign-scope ingress request leaves the existing source owner intact");
        session.Pump(); current.TryPost(State(4)); session.Reset();
        check(!current.TryPost(State(5)) && session.State == null,
            "Reset revokes external producers before retiring the table and rejects their later private snapshots");
        var nextState = state with { Id = "next-external-game" };
        using var next = session.OpenObservationIngress(nextState.Id, nextState.ViewerId);
        next.TryPost(nextState); session.Pump();
        check(session.State?.Id == nextState.Id && session.State.Revision == 1,
            "Queued observations from the prior lifetime cannot resurrect a retired game over the next table");
        var handed = nextState with { ViewerId = 1, Revision = 2, Decision = null };
        session.Accept(handed);
        check(!next.TryPost(nextState with { Revision = 3 }) && session.State?.ViewerId == 1,
            "An accepted viewer handoff revokes the previous audience's ingress instead of mixing private knowledge");
        using var closing = session.OpenObservationIngress(nextState.Id, 1);
        closing.TryPost(handed with { Revision = 3 }); session.Close(); session.Pump();
        check(!closing.TryPost(handed with { Revision = 4 }) && session.State?.Revision == 2
                && Rejected(() => session.OpenObservationIngress(nextState.Id, 1)),
            "Closing a session revokes queued and future source deliveries and cannot reopen through an old recording lease");
        var other = new ClientSession(new Connection(state), observationDelivery: ObservationDeliveryMode.External);
        using var otherSource = other.OpenObservationIngress(state.Id, 0);
        otherSource.TryPost(state); other.Pump();
        check(other.State?.Id == state.Id && session.State?.Id == nextState.Id && !closing.TryPost(state),
            "Separate session capabilities cannot route an old game's projection into a new session or another audience");
        retired.Dispose(); current.Dispose();
    }

    private static async Task ConcurrentProducer(Action<bool, string> check)
    {
        var state = State(1); var session = new ClientSession(new Connection(state), observationDelivery: ObservationDeliveryMode.External);
        using var ingress = session.OpenObservationIngress(state.Id, 0);
        var revisions = new List<long>(); session.Changed += (_, current) => revisions.Add(current!.Revision);
        bool posted = await Task.Run(() => Enumerable.Range(1, 32).All(index => ingress.TryPost(State(index))));
        check(posted && session.State == null, "A background source can safely queue its ordered window without accepting state on the producer thread");
        session.Pump();
        check(revisions.SequenceEqual(Enumerable.Range(1, 32).Select(index => (long)index)),
            "A background source's immutable ordered window is accepted only by the existing owner pump");
    }

    private static async Task AudienceCommandLifetime(Action<bool, string> check)
    {
        foreach (bool replaceGame in new[] { false, true })
        foreach (bool lateFailure in new[] { false, true })
        foreach (bool automatic in new[] { false, true }) {
            var before = State(1);
            before = before with { Decision = before.Decision! with {
                Payload = ((PriorityPayload)before.Decision.Payload) with { CanAutoPass = true } } };
            var late = before with { Revision = 4, Decision = before.Decision with { Id = "old-viewer-next" } };
            var connection = new HeldConnection(late) { ExecuteError = lateFailure ? new IOException("Old audience command failed.") : null };
            var session = new ClientSession(connection, observationDelivery: ObservationDeliveryMode.External);
            session.Accept(before);
            bool started = automatic ? session.Advance(_ => true, (_, _) => false)
                : session.Submit(new PassPriority(before.Id, before.Decision.Id));
            check(started, "The audience lifetime check starts one ordinary scoped " + (automatic ? "Auto pass" : "direct command"));
            await connection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int viewer = replaceGame ? before.ViewerId : 1;
            var handed = before with { Id = replaceGame ? "replacement-game" : before.Id,
                ViewerId = viewer, Revision = 2, DecisionOwnerId = viewer, PriorityPlayerId = viewer,
                Decision = before.Decision with { Id = "new-viewer-current" } };
            check(session.Accept(handed) && session.State == handed && session.Busy,
                "An explicit " + (replaceGame ? "game replacement" : "viewer handoff") + " accepts its audience while retaining the outstanding transport lane");
            using var ingress = session.OpenObservationIngress(handed.Id, handed.ViewerId);
            connection.Release.TrySetResult();
            await Drain(session);
            check(session.State == handed && connection.Commands == 1 && connection.Reads == (lateFailure ? 0 : 1)
                    && !session.NeedsRecovery && !session.IsAdvancing,
                "A prior audience's " + (lateFailure ? "late command failure" : "command receipt")
                    + " cannot replace or fault the accepted audience or continue passing after handoff");
            check(ingress.TryPost(handed with { Revision = 5 }),
                "Discarding an old command receipt preserves the replacement audience's current observation ingress");
            session.Pump();
            check(session.State?.Id == handed.Id && session.State.ViewerId == viewer && session.State.Revision == 5,
                "The replacement audience can continue observing without a second command lane or recovery");
            session.Close();
        }
    }

    private static async Task TransportAudience(Action<bool, string> check)
    {
        foreach (bool foreignGame in new[] { false, true }) {
            foreach (string operation in new[] { "direct", "automatic", "poll" }) {
                var initial = State(1);
                initial = initial with { Decision = initial.Decision! with {
                    Payload = ((PriorityPayload)initial.Decision.Payload) with { CanAutoPass = true } } };
                var foreign = foreignGame ? initial with { Id = "foreign-game", Revision = 2 }
                    : initial with { ViewerId = 1, DecisionOwnerId = 1, Revision = 2 };
                var connection = new Connection(foreign);
                var session = new ClientSession(connection); session.Accept(initial);
                Exception? failure = null; session.Failed += error => failure = error;
                if (operation == "poll") session.Poll(false, _ => throw new InvalidOperationException("A foreign poll must not publish."));
                else check(operation == "automatic" ? session.Advance(_ => true, (_, _) => false)
                    : session.Submit(new PassPriority(initial.Id, initial.Decision.Id)),
                    "A current " + operation + " request begins before testing its transport audience");
                await Drain(session);
                check(session.State == initial && failure is IOException && session.NeedsRecovery
                        && session.UncertainCommand == (operation != "poll")
                        && session.LastFailureWasCommand == (operation != "poll")
                        && connection.Commands == (operation == "poll" ? 0 : 1) && connection.Reads == 1,
                    "A foreign " + (foreignGame ? "game" : "viewer") + " returned by " + operation
                        + " is a connection failure, preserves the owned audience and never supplies another command");
                connection.State = initial with { Revision = 3 };
                session.Poll(false, _ => { }); await Drain(session);
                check(session.State?.ViewerId == initial.ViewerId && session.State.Id == initial.Id
                        && session.State.Revision == 3 && !session.NeedsRecovery
                        && connection.Commands == (operation == "poll" ? 0 : 1),
                    "Fresh observation recovers the original audience without replaying the uncertain " + operation + " request");
                session.Close();
            }
        }
    }

    private static async Task ComposedAudienceLifetimes(Action<bool, string> check)
    {
        foreach (string operation in new[] { "direct", "automatic", "poll" })
        foreach (string sequence in new[] { "viewer-roundtrip", "game-chain", "reset-newgame", "clear-newgame", "viewer-close" })
        foreach (bool lateFailure in new[] { false, true }) {
            var before = State(1);
            before = before with { Decision = before.Decision! with {
                Payload = ((PriorityPayload)before.Decision.Payload) with { CanAutoPass = true } } };
            // A higher late revision and an ABA return to viewer 0 ensure that
            // revision ordering or changed identity alone cannot hide the bug.
            var late = before with { Revision = 99, Decision = before.Decision with { Id = "retired-next" } };
            var port = new HeldConnection(late) { HoldObservation = operation == "poll",
                ExecuteError = lateFailure && operation != "poll" ? new IOException("Retired command failed.") : null,
                ObserveError = lateFailure && operation == "poll" ? new IOException("Retired observation failed.") : null };
            var clock = new Clock(); var session = new ClientSession(port, clock: clock); session.Accept(before);
            string journey = operation + "/" + sequence + "/" + (lateFailure ? "failure" : "receipt");
            if (operation == "poll") {
                session.Poll(false, _ => { }); await port.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            } else {
                check(operation == "automatic" ? session.Advance(_ => true, (_, _) => false)
                    : session.Submit(new PassPriority(before.Id, before.Decision.Id)), "Begin composed lifetime: " + journey);
                await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (operation == "poll" && sequence == "game-chain") {
                clock.Now += TimeSpan.FromSeconds(6);
                check(session.NeedsRecovery, "The held poll is stale before its two explicit audience transitions: " + journey);
            }
            var first = before with { ViewerId = 1, Revision = 2, DecisionOwnerId = 1, PriorityPlayerId = 1,
                Decision = before.Decision with { Id = "first-owner" } };
            GameSnapshot final;
            if (sequence is "clear-newgame" or "reset-newgame") {
                if (sequence == "clear-newgame") check(session.Accept(null), "Explicitly clear the first audience: " + journey);
                else session.Reset();
                final = before with { Id = "after-clear", Revision = 3, Decision = before.Decision with { Id = "after-clear-current" } };
                check(session.State == null && session.Accept(final), "Accept a new game after retiring the visible replica: " + journey);
            } else {
                if (sequence == "game-chain") first = first with { Id = "second-game", ViewerId = 0, DecisionOwnerId = 0, PriorityPlayerId = 0 };
                check(session.Accept(first), "Accept the first audience transition: " + journey);
                final = sequence == "game-chain" ? first with { Id = "third-game", Revision = 3 }
                    : before with { Revision = 3, Decision = before.Decision with { Id = "roundtrip-current" } };
                if (sequence == "viewer-close") { final = first; session.Close(); }
                else check(session.Accept(final), "Accept the second audience transition: " + journey);
            }
            port.Release.TrySetResult(); port.ReleaseObservation.TrySetResult();
            if (!session.Closed) await Drain(session);
            else {
                await (lateFailure && operation != "poll" ? port.ExecuteReturned.Task : port.ObservationReturned.Task).WaitAsync(TimeSpan.FromSeconds(5));
                session.Pump();
            }
            check(session.State == final && !session.NeedsRecovery && !session.IsAdvancing
                    && port.Commands == (operation == "poll" ? 0 : 1)
                    && port.Reads == (lateFailure && operation != "poll" ? 0 : 1),
                "Two lifecycle transitions keep old work from replacing, faulting or acting for the final owner: " + journey);
            if (!session.Closed) session.Close();
        }
    }

    private static async Task BootstrapObservationAdmission(Action<bool, string> check)
    {
        foreach (string result in new[] { "foreign", "missing", "failure" }) {
            var bound = State(1) with { Id = "explicitly-bound-game" };
            var old = State(99) with { Id = "bootstrap-old-game" };
            var port = new HeldConnection(result == "missing" ? null : old) { HoldObservation = true,
                ObserveError = result == "failure" ? new IOException("Old bootstrap read failed.") : null };
            var session = new ClientSession(port); int failures = 0, callbacks = 0;
            session.Failed += _ => failures++;
            session.Poll(false, _ => callbacks++);
            await port.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(session.State == null && session.Polling && port.Commands == 0,
                "A held bootstrap poll starts without an accepted audience: " + result);
            check(session.Accept(bound) && session.State == bound,
                "Explicit acceptance binds a game while its prior bootstrap poll is outstanding: " + result);
            port.ReleaseObservation.TrySetResult(); await Drain(session);
            check(session.State == bound && !session.NeedsRecovery && failures == 0 && callbacks == 0
                    && port.Reads == 1 && port.Commands == 0,
                "A bootstrap " + result + " cannot replace, retire or fault a subsequently bound audience");
            session.Close();
        }
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; }
    }
    private static async Task Drain(ClientSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((session.Busy || session.Polling) && DateTime.UtcNow < deadline) { session.Pump(); await Task.Delay(5); }
        session.Pump();
        if (session.Busy || session.Polling) throw new TimeoutException("External observation command receipt did not settle.");
    }
    private static GameSnapshot State(long revision) => new("external-game", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
         new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("priority-" + revision, new(), new PriorityPayload(new("Pass", true)))) {
            Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority" };
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Connection(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State = state;
        internal int Reads, Commands;
        public Task<GameSnapshot?> ObserveAsync() { Interlocked.Increment(ref Reads); return Task.FromResult<GameSnapshot?>(State); }
        public Task ExecuteAsync(GameCommand command) { Interlocked.Increment(ref Commands); return Task.CompletedTask; }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("An observation test must never concede a game.");
    }
    private sealed class HeldConnection(GameSnapshot? state) : IMatchConnection
    {
        internal int Reads, Commands;
        internal Exception? ExecuteError;
        internal Exception? ObserveError;
        internal bool HoldObservation;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExecuteReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ObservationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseObservation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ObservationReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GameSnapshot?> ObserveAsync() {
            Interlocked.Increment(ref Reads); ObservationStarted.TrySetResult();
            try {
                if (HoldObservation) await ReleaseObservation.Task;
                if (ObserveError != null) throw ObserveError;
                return state;
            } finally { ObservationReturned.TrySetResult(); }
        }
        public async Task ExecuteAsync(GameCommand command) {
            Interlocked.Increment(ref Commands); Started.TrySetResult();
            try { await Release.Task; if (ExecuteError != null) throw ExecuteError; }
            finally { ExecuteReturned.TrySetResult(); }
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("An audience lifetime check must never concede.");
    }
}
