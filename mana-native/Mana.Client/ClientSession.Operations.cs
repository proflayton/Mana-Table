using Mana.Contracts;

namespace Mana.Client;

public sealed partial class ClientSession
{
    // A returned receipt must reach the owner admission/discard boundary even
    // after its epoch retires. Other completion effects require current ownership.
    private abstract record OwnerCompletion
    {
        private OwnerCompletion() { }
        internal sealed record ObservedReceipt(GameSnapshot? State, ClientFailurePhase? FailurePhase,
            bool CommandObservation) : OwnerCompletion;
        internal sealed record CurrentOnlyAction(Action Apply) : OwnerCompletion;
    }
    // One connection operation owns both its wire exchange and the owner-side
    // application. Quiet acknowledgements use this lane without disabling UI.
    private sealed class OperationReservation(bool quiet, ClientFailureContext context)
    {
        internal bool Quiet { get; } = quiet;
        internal ClientFailureContext Context { get; } = context;
        internal bool WireCompleted;
        internal readonly TaskCompletionSource WireFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private abstract record PendingOperation(ClientFailureContext Context)
    {
        private bool terminationClaimed;
        internal bool TryClaimTermination()
        {
            if (terminationClaimed) return false;
            terminationClaimed = true; return true;
        }
    }
    private sealed record PendingGesture(ClientFailureContext Context, GameCommand Command) : PendingOperation(Context);
    private sealed record PendingConnectionRequest(ClientFailureContext Context,
        Func<ClientFailureContext, Task<OwnerCompletion?>> Work) : PendingOperation(Context);
    private sealed record PendingRetirement(PendingOperation Request, ClientDiagnosticFailure Reason);
    private sealed class OperationLane
    {
        internal OperationReservation? Active;
        internal PendingOperation? Pending;
        internal PendingRetirement? Retiring;
        internal bool Occupied => Active != null || Retiring != null;
        internal bool Busy => Active is { Quiet: false } || Pending != null || Retiring != null;
        internal bool TryReserve(OperationReservation owner)
        {
            if (Occupied) return false;
            Active = owner; return true;
        }
        internal bool Release(OperationReservation owner)
        {
            if (!ReferenceEquals(Active, owner)) return false;
            Active = null; return true;
        }
    }
    private readonly OperationLane operations = new();

    private bool StartOwnedOperation(bool quiet, Func<ClientFailureContext, Task<OwnerCompletion?>> work,
        Action<ClientFailureContext, Exception>? failed = null)
    {
        if (Closed || operations.Occupied || Authority != ClientSessionAuthority.Interactive) return false;
        long epoch = quiet ? replica.Epoch : BeginCommand();
        var context = ClientFailureContext.Capture(epoch, State);
        var owner = new OperationReservation(quiet, context);
        if (!operations.TryReserve(owner)) throw new InvalidOperationException("The operation owner changed during synchronous admission.");
        // Teardown waits for actual I/O, never a callback which requires Pump.
        commandTask = owner.WireFinished.Task;
        _ = Task.Run(async () => {
            Exception? failure = null; OwnerCompletion? completion = null;
            try { completion = await work(context); }
            catch (Exception error) { failure = error; }
            finally {
                bool retired;
                lock (observationGate) {
                    owner.WireCompleted = true;
                    retired = Closed;
                    if (retired) operations.Release(owner);
                }
                if (!retired) Post(() => {
                    if (!ReferenceEquals(operations.Active, owner)) return;
                    try {
                        if (failure == null && completion is OwnerCompletion.ObservedReceipt receipt) {
                            Accept(receipt.State, owner.Context.Epoch!.Value, commandObservation: receipt.CommandObservation,
                                observationFailurePhase: receipt.FailurePhase, failureContext: owner.Context);
                        } else if (!Closed && epoch == replica.Epoch) {
                            if (failure == null && completion is OwnerCompletion.CurrentOnlyAction current) current.Apply();
                            else if (failure != null && failed != null) failed(context, failure);
                            else if (failure != null) ReportFailure(failure, ClientFailurePhase.DispatchUnknown, context);
                        }
                    } finally {
                        operations.Release(owner);
                        if (!quiet) { activeSetting = null; Completed?.Invoke(); }
                    }
                });
                owner.WireFinished.TrySetResult();
            }
        });
        return true;
    }

    private bool QueueForeground(PendingOperation request)
    {
        if (operations.Pending != null) return false;
        operations.Pending = request;
        return true;
    }
    private void RetirePendingForeground(ClientDiagnosticFailure reason)
    {
        if (operations.Pending is not { } pending) return;
        operations.Pending = null;
        operations.Retiring = new(pending, reason);
    }
    // Every retired request owns its notifications outside observationGate.
    // A connection request may also have opened a loading/activity owner.
    private void CompleteRetiredForeground()
    {
        if (operations.Retiring is not { } retirement || !retirement.Request.TryClaimTermination()) return;
        var request = retirement.Request;
        try {
            if (request is PendingGesture gesture) {
                try { diagnostics.Decline(State, gesture.Command, retirement.Reason); }
                finally { Completed?.Invoke(); }
                return;
            }
            var failure = new ClientOperationFailure(ClientFailurePhase.DispatchRejected, request.Context,
                new EngineCommandRejectedException("The pending connection request was retired before dispatch. Use the current table."));
            // No exchange ran: do not create or replace recovery provenance.
            try { Failed?.Invoke(failure); }
            finally { Completed?.Invoke(); }
        } finally {
            if (ReferenceEquals(operations.Retiring, retirement)) operations.Retiring = null;
        }
    }
    private void DrainPendingForeground()
    {
        if (operations.Occupied || Polling || Closed || NeedsRecovery || finiteRevocations.Count > 0 || priorityRevocations.Count > 0
            || operations.Pending is not { } pending) return;
        operations.Pending = null;
        bool sameAudience = pending.Context.GameId == null ? State == null
            : State?.Id == pending.Context.GameId && State.ViewerId == pending.Context.ViewerId;
        if (!sameAudience) {
            operations.Retiring = new(pending, ClientDiagnosticFailure.Superseded); CompleteRetiredForeground();
            return;
        }
        if (pending is PendingGesture command) {
            // Recheck the exact current task and source offer. Nothing
            // in the deferred request has entered transport or may be replayed.
            if ((GestureDecline(command.Command) ?? PrepareGesture(command.Command)) is { } reason) {
                operations.Retiring = new(pending, reason); CompleteRetiredForeground();
            } else AdmitPreparedGesture(command.Command);
        } else Run(((PendingConnectionRequest)pending).Work);
    }
}
