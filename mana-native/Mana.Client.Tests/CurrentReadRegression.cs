using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class CurrentReadRegression
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var port = new Port(); var client = new ClientSession(port);
        client.SetPriorityObservationReady(false); client.Accept(port.State);
        var prior = client.TryCaptureCurrentRead();
        check(prior is { IsCurrent: true }, "An idle owner captures current read-only publication ownership");
        client.SetPriorityObservationReady(true);
        try {
            await port.AckEntered.Task.WaitAsync(Guard);
            check(!client.Busy && client.PriorityObservationInFlight && client.TryCaptureCurrentRead() == null && !prior!.IsCurrent,
                "Non-Busy metadata ownership excludes read admission and captured stamps until its complete owner release");
            var newer = port.State with { Revision = 11, BoardRevision = 2,
                PriorityObservation = port.State.PriorityObservation! with { BoardRevision = 2 } };
            port.State = newer; client.Poll(false, _ => { });
            await client.WaitForQueuedWorkAsync().WaitAsync(Guard); client.Pump();
            check(ReferenceEquals(client.State, newer) && client.PriorityObservationInFlight && client.TryCaptureCurrentRead() == null,
                "Ordinary polling remains independent and admits newer authority while the metadata read gate remains closed");
            check(client.Submit(new PassPriority(newer.Id, newer.Decision!.Id)) && client.TryCaptureCurrentRead() == null,
                "A queued explicit gesture retains its existing Busy feedback and excludes observation admission");
            port.ReleaseAck.TrySetResult();
            await client.WaitForQueuedWorkAsync().WaitAsync(Guard);
            check(client.TryCaptureCurrentRead() == null && port.Commands.ToArray() is [AcknowledgePriorityObservation],
                "Returned wire work cannot release read or queued gesture ownership before posted application");
            await Settle(client, () => !client.Busy && !client.PriorityObservationInFlight);
            check(client.TryCaptureCurrentRead() is { IsCurrent: true } && !prior!.IsCurrent
                    && port.Commands.ToArray() is [AcknowledgePriorityObservation, PassPriority],
                "Exactly one queued gesture settles before a fresh read stamp is available, without reviving its predecessor");
        } finally { port.ReleaseAck.TrySetResult(); client.Close(); }

        var orphanPort = new Port(); var orphan = new ClientSession(orphanPort);
        orphan.SetPriorityObservationReady(false);
        orphanPort.State = orphanPort.State with { PriorityObservation = new(1, 0, 1, 0, true, false),
            PriorityContinuation = new(1, "prior-owner", PriorityContinuationMode.NoResponse,
                PriorityContinuationStatus.Active, "no-response", 0, []) };
        orphan.Accept(orphanPort.State);
        check(!orphan.Busy && orphanPort.Commands.IsEmpty && orphan.TryCaptureCurrentRead() == null,
            "Queued negative consent excludes current reads before any withdrawal worker is dispatched");
        try {
            orphan.Pump(); await Settle(orphan, () => !orphan.Busy && !orphan.PriorityContinuationWithdrawing);
            check(orphan.TryCaptureCurrentRead() is { IsCurrent: true } && orphanPort.Commands.ToArray() is [RevokePriorityContinuation],
                "Read readiness returns only after the exact prior-owner withdrawal is applied through its ordinary lane");
        } finally { orphan.Close(); }
        await FailedQuietOperationDoesNotRevive(check);
    }

    private static async Task FailedQuietOperationDoesNotRevive(Action<bool, string> check)
    {
        var port = new Port { FailAck = true }; var client = new ClientSession(port);
        client.SetPriorityObservationReady(false); client.Accept(port.State);
        var captured = client.TryCaptureCurrentRead()!; client.SetPriorityObservationReady(true);
        try {
            await port.AckEntered.Task.WaitAsync(Guard);
            check(!captured.IsCurrent && !captured.HasSameOperation && !captured.HasNewerPublication,
                "Acquiring any quiet reservation retires a captured read without inventing accepted publication credit");
            port.ReleaseAck.TrySetResult();
            await Settle(client, () => !client.Busy && !client.PriorityObservationInFlight);
            check(!captured.IsCurrent && !captured.HasSameOperation && !captured.HasNewerPublication && !client.NeedsRecovery,
                "A certain failed quiet operation cannot revive an old ticket after release without an accepted frame");
            client.SetPriorityObservationReady(false);
            client.Poll(false, _ => { });
            await client.WaitForQueuedWorkAsync().WaitAsync(Guard); client.Pump();
            check(captured.HasNewerPublication && !captured.IsCurrent && client.TryCaptureCurrentRead() is { IsCurrent: true },
                "An accepted ordinary frame separately unlocks fresh discovery after the failed operation and never revives the old ticket");
        } finally { port.ReleaseAck.TrySetResult(); client.Close(); }
    }

    private static async Task Settle(ClientSession client, Func<bool> done)
    {
        for (int step = 0; step < 32; step++) {
            client.Pump(); if (done()) return;
            await client.WaitForQueuedWorkAsync().WaitAsync(Guard);
        }
        throw new InvalidOperationException("The bounded current-read owner did not settle.");
    }

    private sealed class Port : IMatchConnection
    {
        internal GameSnapshot State = new("read-admission", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("read-input", new(), new PriorityPayload(new("Pass", true)))) {
                Revision = 10, BoardRevision = 1,
                PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                PriorityObservation = new(1, 0, 0, 0, false, false)
            };
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal readonly TaskCompletionSource AckEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailAck;
        public bool SupportsPriorityContinuation => true;
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string id) => throw new InvalidOperationException("No synthetic concession.");
        public async Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (command is AcknowledgePriorityObservation ack) {
                AckEntered.TrySetResult(); await ReleaseAck.Task;
                if (FailAck) throw new EngineCommandRejectedException("Controlled certain quiet rejection.");
                State = State with { PriorityObservation = State.PriorityObservation! with {
                    Registered = true, AcknowledgedBoardRevision = ack.BoardRevision,
                    AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } };
            } else if (command is PassPriority) State = State with { Revision = State.Revision + 1, Decision = null };
            else if (command is RevokePriorityContinuation revoke) State = State with {
                PriorityContinuation = State.PriorityContinuation! with {
                    Sequence = revoke.ThroughSequence, Status = PriorityContinuationStatus.Revoked } };
            else throw new InvalidOperationException("Unexpected current-read control command.");
        }
    }
}
