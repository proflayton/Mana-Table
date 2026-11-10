using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

internal static class PolledPlayerAttemptChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await RetiredSourceThenFreshInput(check);
        await UnmatchedWireAck(check);
    }

    private static async Task RetiredSourceThenFreshInput(Action<bool, string> check)
    {
        var inner = new Port();
        var ledger = new PolledPlayerAttemptLedger();
        var port = new PolledLobbyPorts.ReadPort(inner) { Acknowledged = ledger.Acknowledged };
        var client = new ClientSession(port, sourceActions: new SourcePort()); ledger.Attach(client);
        client.SetPriorityObservationReady(false); client.Accept(inner.State);
        var state = client.State!; var source = state.Viewer!.Zone("Battlefield").Cards.Single();
        var context = new SourceActionContext(state.Id, state.Decision!.Id, state.Revision, state.ViewerId, source.Key, source.VisualId);
        client.SourceActions.Observe(context);
        await Until(client, () => client.SourceActions.View.Status == SourceActionStatus.Ready);
        client.SetPriorityObservationReady(true); client.Pump();
        try {
            await inner.AckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var command = new UseOfferedAction(state.Id, state.Decision.Id, source.Key, "offered");
            check(ledger.Submit(state, command) && client.Busy && client.PriorityObservationInFlight,
                "The real polled ledger admits one exact source gesture queued behind the client's quiet ACK");
            check(ledger.Capture() is { Admitted: 1, Acknowledged: 0, RetiredBeforeDispatch: 0, Live: 1, Conserved: true }
                    && !ledger.Reconciled(0) && port.InputAccepted == 0,
                "A queued local admission remains live and cannot count as a provider input or a reconciled terminal receipt");
            var decline = Decline(state);
            ledger.ObserveDiagnostic(decline with { Origin = ClientDiagnosticOrigin.Automatic });
            ledger.ObserveDiagnostic(decline with { Command = ClientDiagnosticCommand.PassPriority });
            ledger.ObserveDiagnostic(decline with { Facts = decline.Facts with { ViewerId = 1 } });
            ledger.ObserveDiagnostic(decline with { Facts = decline.Facts with { Revision = state.Revision - 1 } });
            check(ledger.Capture() is { Admitted: 1, RetiredBeforeDispatch: 0, Live: 1, Failure: null },
                "Automatic, other-command, foreign-audience and older-scope declines cannot retire the live exact foreground gesture");
            inner.State = inner.State with { Revision = state.Revision + 1 }; client.Accept(inner.State);
            inner.ReleaseAck.TrySetResult();
            await Until(client, () => !client.Busy && !client.PriorityObservationInFlight);
            ledger.RequireHealthy();
            var retired = ledger.Capture();
            check(retired is { Admitted: 1, Acknowledged: 0, RetiredBeforeDispatch: 1, Live: 0, Conserved: true }
                    && retired.Receipts is [{ Sequence: 1, Command: nameof(UseOfferedAction), Outcome: "retired-before-dispatch",
                        Retirement: ClientDiagnosticFailure.SourceOfferChanged, DispatchAttempt: null }]
                    && retired.Receipts[0].Decision == command.DecisionId
                    && retired.Receipts[0].Revision == state.Revision
                    && port.InputAccepted == 0 && ledger.Reconciled(port.InputAccepted)
                    && inner.Commands.ToArray() is [AcknowledgePriorityObservation],
                "Actual source-offer revalidation terminates its original sequence with typed predispatch retirement and zero gameplay wire commands");
            var fresh = client.State!;
            var pass = new PassPriority(fresh.Id, fresh.Decision!.Id);
            check(ledger.Submit(fresh, pass), "The retired source attempt leaves room for a separately admitted fresh ordinary command");
            await Until(client, () => !client.Busy);
            ledger.RequireHealthy();
            var complete = ledger.Capture();
            check(complete is { Admitted: 2, Acknowledged: 1, RetiredBeforeDispatch: 1, Live: 0, Conserved: true }
                    && complete.Receipts[1] is { Sequence: 2, Command: nameof(PassPriority), Outcome: "acknowledged", Retirement: null, DispatchAttempt: not null }
                    && port.ReceiptCounts is { Inputs: 1, ObservationAcknowledgements: 1, Conserved: true }
                    && ledger.Reconciled(port.InputAccepted) && client.State!.Decision == null,
                "A fresh real client command has its own wire ACK and owner completion; all admitted attempts conserve without requiring a retired gesture to reach Forge");
            ledger.Acknowledged(pass, fresh);
            check(ledger.Capture() is { Admitted: 2, Acknowledged: 1, RetiredBeforeDispatch: 1, Live: 0, Failure: not null, Conserved: true }
                    && !ledger.Reconciled(port.InputAccepted),
                "A duplicate terminal ACK cannot count twice or leave the polled ledger qualified");
        } finally { inner.ReleaseAck.TrySetResult(); client.Close(); }
    }

    private static async Task UnmatchedWireAck(Action<bool, string> check)
    {
        var inner = new Port { HoldInput = true };
        inner.State = inner.State with { PriorityObservation = null, PriorityContinuation = null };
        var ledger = new PolledPlayerAttemptLedger();
        var port = new PolledLobbyPorts.ReadPort(inner) { Acknowledged = ledger.Acknowledged };
        var client = new ClientSession(port); ledger.Attach(client); client.Accept(inner.State);
        var pass = new PassPriority(inner.State.Id, inner.State.Decision!.Id);
        try {
            check(ledger.Submit(inner.State, pass), "The unmatched-ACK control owns a real held foreground dispatch");
            await inner.InputStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ledger.Acknowledged(new PassPriority(pass.GameId, pass.DecisionId), inner.State);
            check(ledger.Capture() is { Admitted: 1, Acknowledged: 0, RetiredBeforeDispatch: 0, Live: 1, Failure: not null, Conserved: true }
                    && !ledger.Reconciled(0),
                "An equal-looking command instance cannot borrow the held attempt's ACK or clear its live identity");
            inner.ReleaseInput.TrySetResult(); await Until(client, () => !client.Busy);
            check(!ledger.Reconciled(port.InputAccepted) && ledger.Capture().Failure != null,
                "A later genuine ACK cannot erase an earlier receipt-integrity failure");
        } finally { inner.ReleaseInput.TrySetResult(); client.Close(); }
    }

    private static ClientDiagnostic Decline(GameSnapshot state) => new(999, ClientDiagnosticOrigin.Direct,
        ClientDiagnosticStage.LocalDeclined, ClientDiagnosticCommand.UseOfferedAction, ClientDiagnosticFailure.SourceOfferChanged,
        null, new(true, state.ViewerId, state.Revision, ClientDiagnosticDecision.Priority, state.ViewerId,
            null, null, null, null, null, [], false));
    private static async Task Until(ClientSession client, Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do { client.Pump(); await Task.Delay(1); } while (!completed() && DateTime.UtcNow < deadline);
        client.Pump();
        if (!completed()) throw new TimeoutException("The controlled polled foreground owner did not settle.");
    }
    private sealed class SourcePort : ISourceActions
    {
        public bool SupportsSourceActions => true;
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string key)
            => Task.FromResult(new SourceActionSet(gameId, decisionId, key, "source", [new("offered", "activate", "Exact source")]));
    }
    private sealed class Port : IMatchConnection
    {
        public bool SupportsPriorityContinuation => true;
        internal GameSnapshot State = new("polled-attempt", "playing", null, null, 0, 2, "Main", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [new Card { Name = "Source", Key = "source-key", VisualId = "source", Selectable = true }], null)])],
            [], null, new("priority", new(), new PriorityPayload(new("Pass", true)))) {
                Revision = 10, BoardRevision = 1,
                PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                PriorityObservation = new(1, 0, 0, 0, false, false)
            };
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal readonly TaskCompletionSource AckStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource InputStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldInput;
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public async Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (command is AcknowledgePriorityObservation ack) {
                AckStarted.TrySetResult(); await ReleaseAck.Task;
                State = State with { PriorityObservation = State.PriorityObservation! with { Registered = true,
                    AcknowledgedBoardRevision = ack.BoardRevision, AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } };
            } else if (command is PassPriority) {
                InputStarted.TrySetResult(); if (HoldInput) await ReleaseInput.Task;
                State = State with { Revision = State.Revision + 1, Decision = null };
            } else throw new InvalidDataException("The retired source command must never reach the controlled provider.");
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
