using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

// The receipt observer must distinguish answers from delivery metadata without
// removing either from transport diagnostics or replacing a real polled owner.
internal static class PolledInputReceiptChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await PolledPlayerAttemptChecks.RunAsync(check);
        GameCommand[] controls = [
            new GrantPriorityContinuation("game", "priority", 1, "grant", PriorityContinuationMode.NoResponse, []),
            new RevokePriorityContinuation("game", 1),
            new GrantFiniteStackYield("game", "priority", "finite", ["execution"], 0, 0),
            new RevokeFiniteStackYield("game", "finite"),
            new IgnorePriorityAbility("game", "priority", "ability"), new RestorePriorityAbility("game", "ability"),
            new UndoAction("game", "priority", "offer", "action", 0), new UndoLastAction("game", "priority"),
            new PassForNow("game", "priority", "original"), new PassTurn("game", "priority")
        ];
        check(controls.All(command => PolledLobbyPorts.ClassifyPlayerReceipt(command) == PolledLobbyPorts.PlayerReceiptKind.Control),
            "Yield grants, withdrawal, preferences and correction controls cannot count as an ordinary scripted input success");
        var inner = new ControlledPort();
        var port = new PolledLobbyPorts.ReadPort(inner);
        var client = new ClientSession(port);
        client.Accept(inner.State);
        client.Pump();
        await inner.MetadataReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(client.PriorityObservationInFlight && !client.Busy && !port.TryArmOrdinaryPoll(client) && !port.Captured.Task.IsCompleted,
            "An in-flight observer metadata readback cannot arm or steal the intended held ordinary poll");
        inner.ReleaseMetadataRead.TrySetResult();
        await Until(client, () => !client.PriorityObservationInFlight);
        check(port.ReceiptCounts is { Total: 1, Inputs: 0, ObservationAcknowledgements: 1, Controls: 0, Conserved: true }
            && port.CommandLatencies.Single().Kind == nameof(AcknowledgePriorityObservation),
            "The observer ACK retains its actual provider receipt and latency while contributing zero scripted inputs");
        check(port.TryArmOrdinaryPoll(client), "The ordinary-poll latch arms after the actual metadata operation has settled");
        client.Poll(false, _ => { });
        await port.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(client.Polling && !client.Busy && port.InputAcceptedAtCapture == 0 && port.AcceptedAtCapture == 1,
            "The captured ordinary poll retains separate input and total receipt witnesses");
        check(client.Submit(new PassPriority(inner.State.Id, inner.State.Decision!.Id)),
            "The actual scripted answer can advance while its earlier ordinary poll remains held");
        await Until(client, () => !client.Busy);
        var answered = client.State;
        check(port.ReceiptCounts is { Total: 2, Inputs: 1, ObservationAcknowledgements: 1, Controls: 0, Conserved: true }
            && client.State?.Decision == null && port.CommandLatencies.Count == 2,
            "One scripted admission reconciles exactly one input receipt despite the earlier independent observer ACK");
        port.Release.TrySetResult();
        await Until(client, () => !client.Polling);
        check(ReferenceEquals(client.State, answered), "Releasing the older held poll cannot overwrite the later scripted input receipt");
        int inputs = port.InputAccepted;
        await port.ExecuteAsync(new RestorePriorityAbility(inner.State.Id, "preference"));
        check(port.ReceiptCounts is { Total: 3, Inputs: 1, ObservationAcknowledgements: 1, Controls: 1, Conserved: true }
            && port.InputAccepted == inputs && port.CommandLatencies.Count == 3,
            "An accepted control remains recorded but cannot satisfy a resumed-input witness");
        inner.FailNext = true;
        bool rejected = false;
        try { await port.ExecuteAsync(new PassPriority(inner.State.Id, "changed")); }
        catch (EngineCommandRejectedException) { rejected = true; }
        check(rejected && port.Failures == 1 && port.ReceiptCounts is { Total: 3, Inputs: 1, Conserved: true },
            "A rejected command increments no receipt class and preserves exact accounting");
        client.Close();
    }

    private static async Task Until(ClientSession client, Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do { client.Pump(); await Task.Delay(1); } while (!complete() && DateTime.UtcNow < deadline);
        client.Pump();
        if (!complete()) throw new TimeoutException("The controlled receipt/latch owner did not settle.");
    }

    private sealed class ControlledPort : IMatchConnection
    {
        public bool SupportsPriorityContinuation => true;
        internal GameSnapshot State = new("receipt-game", "playing", null, null, 0, 2, "Main phase", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("priority", new(), new PriorityPayload(new("Pass once", true)))) {
            Revision = 1, BoardRevision = 1,
            PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
            PriorityObservation = new(1, 0, 0, 0, false, false)
        };
        private int holdMetadata = 1;
        internal bool FailNext;
        internal TaskCompletionSource MetadataReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMetadataRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GameSnapshot?> ObserveAsync()
        {
            var captured = State;
            if (Interlocked.Exchange(ref holdMetadata, 0) == 1) {
                MetadataReadStarted.TrySetResult(); await ReleaseMetadataRead.Task;
            }
            return captured;
        }
        public Task ExecuteAsync(GameCommand command)
        {
            if (FailNext) { FailNext = false; throw new EngineCommandRejectedException("Controlled input rejection"); }
            if (command is AcknowledgePriorityObservation ack) State = State with {
                PriorityObservation = State.PriorityObservation! with { Registered = true,
                    AcknowledgedBoardRevision = ack.BoardRevision, AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } };
            else if (command is PassPriority) State = State with { Revision = State.Revision + 1, Decision = null };
            else if (command is not RestorePriorityAbility) throw new InvalidOperationException("Unexpected controlled input receipt");
            return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
