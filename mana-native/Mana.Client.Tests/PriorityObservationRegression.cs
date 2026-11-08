using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class PriorityObservationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var port = new Port(); var session = new ClientSession(port);
        session.SetPriorityObservationReady(false); session.Accept(port.State);
        session.Pump();
        check(!session.PriorityObservationInFlight && !session.Busy && port.Commands.IsEmpty,
            "An unpresented accepted baseline grants no server observation credit");
        session.SetPriorityObservationReady(true); session.Pump();
        await Until(session, () => port.AckStarted.Task.IsCompleted);
        check(session.PriorityObservationInFlight && !session.Busy && !session.Polling,
            "Initial observer registration uses one quiet metadata task without disabling gameplay controls");
        for (int frame = 0; frame < 100; frame++) session.Pump();
        check(port.Commands.Count == 1, "A held metadata receipt remains one coalesced acknowledgement rather than a per-poll feedback loop");
        port.ReleaseAck.TrySetResult(); await Until(session, () => !session.PriorityObservationInFlight);
        for (int frame = 0; frame < 100; frame++) session.Pump();
        check(port.Commands.Count == 1 && session.State!.PriorityObservation!.Registered,
            "An acknowledged unchanged baseline does not send another observer message");

        port.Publish(15); session.Accept(port.State); session.Pump();
        check(port.Commands.Count == 1, "Fifteen later accepted events do not produce metadata traffic below the coalescing bound");
        port.Publish(16); session.SetPriorityObservationReady(false); session.Accept(port.State); session.Pump();
        check(port.Commands.Count == 1, "A new coalesced prefix still waits until its actual presentation is ready");
        session.SetPriorityObservationReady(true); session.Pump(); await Until(session, () => !session.PriorityObservationInFlight);
        check(port.Commands.OfType<AcknowledgePriorityObservation>().Last().PublicActivitySequence == 16,
            "Ready presentation returns exactly the accepted public activity prefix");

        port.Publish(17, "awaiting-observation"); session.Accept(port.State); session.Pump();
        await Until(session, () => !session.PriorityObservationInFlight);
        check(port.Commands.OfType<AcknowledgePriorityObservation>().Last().PublicActivitySequence == 17,
            "A server observation hold flushes a ready smaller prefix without waiting for sixteen more events");
        port.Publish(33);
        session.Post(() => session.Accept(port.State));
        session.SetPriorityObservationReady(false); session.Pump();
        check(!session.PriorityObservationInFlight && port.Commands.OfType<AcknowledgePriorityObservation>().Last().PublicActivitySequence == 17,
            "Closing readiness before the owner pump prevents new accepted activity from borrowing the preceding frame's reading credit");
        session.SetPriorityObservationReady(true);
        await Until(session, () => !session.PriorityObservationInFlight);
        check(port.Commands.OfType<AcknowledgePriorityObservation>().Last().PublicActivitySequence == 33,
            "The completed presentation opens and flushes its current accepted prefix without another polling tick");
        session.Close();

        port = new Port(); session = new(port); session.Accept(port.State); session.Pump();
        await Until(session, () => port.AckStarted.Task.IsCompleted);
        check(session.Submit(new PassPriority(port.State.Id, port.State.Decision!.Id)),
            "An outstanding observer acknowledgement cannot block an explicit gameplay answer");
        await Until(session, () => !session.Busy);
        var answered = session.State;
        port.ReleaseAck.TrySetResult(); await Until(session, () => !session.PriorityObservationInFlight);
        check(session.State!.Revision == answered!.Revision && session.State.Decision == null && !session.NeedsRecovery,
            "A late metadata result from the earlier epoch cannot clobber the explicit answer or restore its old prompt");
        session.Close();

        port = new Port(); session = new(port); session.Accept(port.State);
        check(session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, []) && session.PriorityContinuationPending,
            "An Auto request reserves intent while its observer baseline still needs registration");
        session.Pump(); await Until(session, () => port.AckStarted.Task.IsCompleted);
        check(port.Commands is not null && !port.Commands.OfType<GrantPriorityContinuation>().Any(),
            "Native Auto never sends a positive grant before actual host observation registration");
        port.ReleaseAck.TrySetResult();
        await Until(session, () => !session.PriorityContinuationPending && !session.Busy && !session.PriorityObservationInFlight);
        check(port.Commands.ToArray() is [AcknowledgePriorityObservation, GrantPriorityContinuation],
            "The accepted baseline receipt enables exactly one requested server grant without rejected retries");
        session.Close();

        await AfterEliminationAsync(check);
        await RetiredObserverAsync(check);
        IneligibleObservers(check);
    }

    private static async Task AfterEliminationAsync(Action<bool, string> check)
    {
        var port = new Port { TrimAcknowledgedHistory = true }; var session = new ClientSession(port);
        session.SetPriorityObservationReady(false); session.Accept(port.State);
        port.ReleaseAck.TrySetResult(); session.SetPriorityObservationReady(true);
        await Until(session, () => !session.PriorityObservationInFlight);
        var actor = session.State!;
        var pass = new PassPriority(actor.Id, actor.Decision!.Id);
        var grant = new GrantPriorityContinuation(actor.Id, actor.Decision.Id, 1, "lost-actor", PriorityContinuationMode.NoResponse, []);
        check(actor.PriorityObservation!.Registered && GameCommandPermissions.Allows(actor, pass)
            && GameCommandPermissions.Allows(actor, grant),
            "A live observer registers its delivery window while retaining its actual offered actor commands");

        session.SetPriorityObservationReady(false);
        port.State = Eliminated(actor, 160); session.Accept(port.State); session.Pump();
        check(port.Commands.Count == 1 && session.State!.Activity.Length == 160,
            "Elimination does not let an unpresented delivery prefix borrow earlier reading credit");
        check(!session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, [])
            && !session.Submit(grant) && !session.Submit(pass)
            && !session.AdvanceQuietly("lost-window", _ => true, (_, _) => false),
            "An eliminated observer cannot register positive consent, answer its retired prompt or start quiet policy work");
        session.SetPriorityObservationReady(true);
        await Until(session, () => !session.PriorityObservationInFlight);
        check(port.Commands.ToArray() is [AcknowledgePriorityObservation, AcknowledgePriorityObservation { BoardRevision: 2, PublicActivitySequence: 160 }]
            && session.State is { Viewer.Eliminated: true, Decision: null,
                PriorityObservation: { Registered: true, AcknowledgedBoardRevision: 2, AcknowledgedPublicActivitySequence: 160 } }
            && session.State.Activity.Length == 120 && session.State.Activity[0].Id == 41,
            "A registered viewer continues acknowledging after elimination so the provider can drain its delivered history suffix without reviving an input");
        for (int frame = 0; frame < 100; frame++) session.Pump();
        check(port.Commands.Count == 2 && !session.PriorityContinuationReserved && !session.NeedsRecovery,
            "The eliminated observer's acknowledged unchanged frame creates no ACK loop or gameplay authority");
        session.Close();
    }

    private static async Task RetiredObserverAsync(Action<bool, string> check)
    {
        foreach (bool close in new[] { false, true }) {
            var port = new Port(); port.State = Eliminated(port.State, 160);
            var session = new ClientSession(port); session.Accept(port.State); session.Pump();
            await Until(session, () => port.AckStarted.Task.IsCompleted);
            var replacement = port.State with { ViewerId = 1, Revision = port.State.Revision + 1,
                PriorityObservation = port.State.PriorityObservation! with {
                    AcknowledgedBoardRevision = 2, AcknowledgedPublicActivitySequence = 160 } };
            if (close) session.Close(); else session.Accept(replacement);
            port.ReleaseAck.TrySetResult();
            if (close) { await port.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)); session.Pump(); }
            else await Until(session, () => !session.PriorityObservationInFlight);
            check((close ? session.Closed && session.State!.ViewerId == 0 : ReferenceEquals(session.State, replacement))
                && !session.NeedsRecovery && port.Commands.Count == 1,
                "A held eliminated-viewer ACK cannot restore its audience or emit another RPC after " + (close ? "close" : "audience replacement"));
            if (!close) session.Close();
        }
    }

    private static void IneligibleObservers(Action<bool, string> check)
    {
        var port = new Port(); var lost = Eliminated(port.State, 160);
        foreach (var (name, state, authority, supported, ready, closed) in new[] {
            ("ObserveOnly", lost, ClientSessionAuthority.ObserveOnly, true, true, false),
            ("closed", lost, ClientSessionAuthority.Interactive, true, true, true),
            ("unpresented", lost, ClientSessionAuthority.Interactive, true, false, false),
            ("unsupported", lost, ClientSessionAuthority.Interactive, false, true, false),
            ("missing viewer", lost with { ViewerId = 99 }, ClientSessionAuthority.Interactive, true, true, false),
            ("completed match", lost with { Status = "finished" }, ClientSessionAuthority.Interactive, true, true, false),
            ("failed match", lost with { Status = "error" }, ClientSessionAuthority.Interactive, true, true, false),
            ("overflowed delivery", lost with { PriorityObservation = lost.PriorityObservation! with { Overflowed = true } },
                ClientSessionAuthority.Interactive, true, true, false)
        }) {
            port = new Port { State = state, SupportsPriorityContinuation = supported };
            var session = new ClientSession(port, authority: authority);
            session.SetPriorityObservationReady(false); session.Accept(state);
            if (closed) session.Close();
            session.SetPriorityObservationReady(ready); session.Pump();
            check(port.Commands.IsEmpty && !session.PriorityObservationInFlight,
                "An eliminated " + name + " audience sends no observation RPC");
            if (!closed) session.Close();
        }

        port = new Port { State = lost with { PriorityObservation = lost.PriorityObservation! with {
            AcknowledgedBoardRevision = 2, AcknowledgedPublicActivitySequence = 160 } } };
        var current = new ClientSession(port); current.Accept(port.State);
        check(!current.Accept(lost with { Revision = lost.Revision - 1 }),
            "A stale eliminated-viewer publication cannot replace an already acknowledged delivery window");
        current.Pump();
        check(port.Commands.IsEmpty && ReferenceEquals(current.State, port.State),
            "Refusing stale history cannot manufacture a new observer acknowledgement");
        current.Close();
    }

    private static GameSnapshot Eliminated(GameSnapshot state, int sequence) => state with {
        Revision = state.Revision + 1, BoardRevision = 2, Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1,
        Players = [state.Viewer! with { Eliminated = true, Life = -1, Priority = false },
            new(1, "Still playing", 40, true, false, ImmutableDictionary<string, int>.Empty, [])],
        PriorityObservation = state.PriorityObservation! with { BoardRevision = 2, PublicActivitySequence = sequence, Registered = true },
        Activity = Enumerable.Range(1, sequence).Select(id => new ActivityEntry(id, state.Turn, state.PhaseKey,
            "phase", 1, "Published table activity", null, null)).ToImmutableArray()
    };

    private static async Task Until(ClientSession session, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) { session.Pump(); await Task.Delay(1); }
        session.Pump();
        if (!done()) throw new TimeoutException("The controlled observer metadata task did not finish.");
    }
    private sealed class Port : IMatchConnection
    {
        internal GameSnapshot State = new("observation-game", "playing", null, null, 0, 2, "Main phase", "MAIN1", 1,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("priority", new(), new PriorityPayload(new("Pass once", true)))) {
                Revision = 10, BoardRevision = 1,
                PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                PriorityObservation = new(1, 0, 0, 0, false, false)
            };
        public bool SupportsPriorityContinuation { get; init; } = true;
        internal bool TrimAcknowledgedHistory;
        internal ConcurrentQueue<GameCommand> Commands = new();
        internal TaskCompletionSource AckStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (command is AcknowledgePriorityObservation ack) {
                AckStarted.TrySetResult(); await ReleaseAck.Task;
                State = State with { PriorityObservation = State.PriorityObservation! with {
                    Registered = true, AcknowledgedBoardRevision = ack.BoardRevision,
                    AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } };
                if (TrimAcknowledgedHistory) State = State with { Activity = State.Activity
                    .Where(entry => entry.Id > ack.PublicActivitySequence - 120).ToImmutableArray() };
            } else if (command is GrantPriorityContinuation grant) State = State with {
                Revision = State.Revision + 1, PriorityContinuation = new(grant.Sequence, grant.GrantId, grant.Mode,
                    PriorityContinuationStatus.Held, "response-available", 0, grant.PhaseStops) };
            else if (command is RevokePriorityContinuation revoke) State = State with { Revision = State.Revision + 1,
                PriorityContinuation = State.PriorityContinuation! with { Sequence = revoke.ThroughSequence, Status = PriorityContinuationStatus.Revoked } };
            else if (command is PassPriority) State = State with { Revision = State.Revision + 1, Decision = null };
            else throw new InvalidOperationException("Unexpected observer command");
        }
        internal void Publish(long sequence, string reason = "")
            => State = State with { Revision = State.Revision + 1,
                PriorityObservation = State.PriorityObservation! with { PublicActivitySequence = sequence },
                PriorityContinuation = State.PriorityContinuation! with { ReasonCode = reason } };
        public Task<GameSnapshot?> ObserveAsync() { ReadCompleted.TrySetResult(); return Task.FromResult<GameSnapshot?>(State); }
        public Task ConcedeAsync(string game) => Task.CompletedTask;
    }
}
