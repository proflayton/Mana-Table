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
}
