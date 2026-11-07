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
    }

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
        public bool SupportsPriorityContinuation => true;
        internal ConcurrentQueue<GameCommand> Commands = new();
        internal TaskCompletionSource AckStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (command is AcknowledgePriorityObservation ack) {
                AckStarted.TrySetResult(); await ReleaseAck.Task;
                State = State with { PriorityObservation = State.PriorityObservation! with {
                    Registered = true, AcknowledgedBoardRevision = ack.BoardRevision,
                    AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } };
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
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string game) => Task.CompletedTask;
    }
}
