using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

// The prefix owns one foreground submitter per ClientSession. A successful
// Submit may wait behind a quiet owner and terminate before reaching transport.
// Keep that exact live command until the existing foreground completion hook;
// diagnostic summaries are never reconstructed into command authority.
internal sealed class PolledPlayerAttemptLedger
{
    internal sealed record Receipt(long Sequence, string GameId, int ViewerId, long Revision, long BoardRevision, string? Decision,
        string Command, string Outcome, ClientDiagnosticFailure? Retirement, long? DispatchAttempt, long? RetirementDiagnostic);
    internal sealed record Accounting(int Admitted, int Acknowledged, int RetiredBeforeDispatch, int Live,
        string? Failure, Receipt? Current, Receipt[] Receipts)
    {
        internal bool Conserved => Admitted == Acknowledged + RetiredBeforeDispatch + Live;
    }
    private sealed class Attempt(long sequence, GameSnapshot state, GameCommand command, ClientDiagnosticCommand kind)
    {
        internal readonly long Sequence = sequence;
        internal readonly GameSnapshot State = state;
        internal readonly GameCommand Command = command;
        internal readonly ClientDiagnosticCommand Kind = kind;
        internal bool Admitted, Dispatched, Acknowledged;
        internal long? DispatchAttempt;
        internal long? RetirementDiagnostic;
        internal ClientDiagnosticFailure? Retirement;
    }
    private readonly object gate = new();
    private readonly List<Receipt> receipts = [];
    private Attempt? live;
    private ClientSession? owner;
    private int admitted, acknowledged, retired;
    private long sequence;
    private string? failure;

    internal void Attach(ClientSession client)
    {
        lock (gate) {
            if (owner != null) throw new InvalidOperationException("The attempt ledger already has an owner.");
            owner = client;
            client.DiagnosticRecorded += ObserveDiagnostic;
            client.Completed += Complete;
        }
    }
    internal bool Submit(GameSnapshot state, GameCommand command)
    {
        lock (gate) {
            RequireHealthy();
            if (owner == null || live != null) throw new InvalidDataException("A polled foreground attempt still owns its terminal result.");
            if (receipts.Count >= 4096) throw new InvalidDataException("The bounded polled attempt ledger is full.");
            live = new(++sequence, state, command, Kind(command));
            // Hold the ledger gate through synchronous admission. Dispatch
            // workers may already be running when Submit returns.
            if (!owner.Submit(command)) { live = null; return false; }
            live.Admitted = true; admitted++;
            return true;
        }
    }
    internal Accounting Capture()
    {
        lock (gate) return new(admitted, acknowledged, retired, live is { Admitted: true } ? 1 : 0,
            failure, live == null ? null : Describe(live, "live"), receipts.ToArray());
    }
    internal int AdmittedCount { get { lock (gate) return admitted; } }
    internal bool Reconciled(int providerInputs)
    {
        lock (gate) return failure == null && live == null && admitted == acknowledged + retired
            && acknowledged == providerInputs;
    }
    internal void RequireHealthy()
    {
        lock (gate) if (failure != null) throw new InvalidDataException(failure);
    }
    // ReadPort supplies the actual command instance which returned from the
    // provider. Quiet ACKs and settings retain their independent receipt class.
    internal void Acknowledged(GameCommand command, GameSnapshot? before)
    {
        if (PolledLobbyPorts.ClassifyPlayerReceipt(command) != PolledLobbyPorts.PlayerReceiptKind.Input) return;
        lock (gate) {
            if (live is not { } attempt || !ReferenceEquals(attempt.Command, command)
                || !attempt.Dispatched || attempt.Acknowledged || attempt.Retirement != null) {
                Fail("An unmatched or duplicate polled input ACK cannot terminate a foreground attempt."); return;
            }
            attempt.Acknowledged = true;
        }
    }
    internal void ObserveDiagnostic(ClientDiagnostic diagnostic)
    {
        if (diagnostic.Origin != ClientDiagnosticOrigin.Direct) return;
        lock (gate) {
            if (live is not { } attempt || diagnostic.Command != attempt.Kind) return;
            if (diagnostic.Stage == ClientDiagnosticStage.LocalDeclined) {
                // This owner is the fixture's sole foreground submitter. A
                // current typed decline plus its following Completed belongs
                // to that one queued command, never an unrelated diagnostic.
                if (!attempt.Admitted || !diagnostic.Facts.HasSnapshot
                    || diagnostic.Facts.ViewerId != attempt.State.ViewerId
                    || diagnostic.Facts.Revision is not { } revision || revision < attempt.State.Revision) return;
                if (attempt.Dispatched || attempt.Acknowledged || attempt.Retirement != null) {
                    Fail("A dispatched or already retired polled attempt cannot retire before dispatch."); return;
                }
                if (diagnostic.Failure is not (ClientDiagnosticFailure.SourceOfferChanged
                    or ClientDiagnosticFailure.NotOffered or ClientDiagnosticFailure.Superseded) || diagnostic.AttemptId == null) {
                    Fail("An unhealthy foreground retirement cannot count as safe polled progress: " + diagnostic.Failure); return;
                }
                attempt.Retirement = diagnostic.Failure;
                attempt.RetirementDiagnostic = diagnostic.AttemptId;
                return;
            }
            if (diagnostic.Stage == ClientDiagnosticStage.Admitted) {
                if (attempt.DispatchAttempt != null || diagnostic.AttemptId == null || attempt.Retirement != null) {
                    Fail("A polled attempt cannot acquire a second or retired dispatch identity."); return;
                }
                attempt.DispatchAttempt = diagnostic.AttemptId;
            } else if (diagnostic.Stage == ClientDiagnosticStage.DispatchStarted) {
                if (attempt.DispatchAttempt != diagnostic.AttemptId || diagnostic.AttemptId == null
                    || attempt.Dispatched || attempt.Retirement != null) {
                    Fail("A polled dispatch must match its one existing foreground attempt."); return;
                }
                attempt.Dispatched = true;
            } else if (diagnostic.Stage is ClientDiagnosticStage.ProviderRejected or ClientDiagnosticStage.OutcomeUnknown) {
                Fail("A failed or uncertain polled foreground command cannot qualify: " + diagnostic.Stage);
            }
        }
    }
    private void Complete()
    {
        lock (gate) {
            if (live is not { Admitted: true } attempt) { Fail("A foreground completion has no admitted polled attempt."); return; }
            string outcome;
            if (attempt.Acknowledged && attempt.Dispatched && attempt.Retirement == null) {
                acknowledged++; outcome = "acknowledged";
            } else if (attempt.Retirement != null && !attempt.Dispatched && !attempt.Acknowledged && attempt.DispatchAttempt == null) {
                retired++; outcome = "retired-before-dispatch";
            } else { Fail("A polled foreground completion lacks an exact ACK or typed predispatch retirement."); return; }
            receipts.Add(Describe(attempt, outcome));
            live = null;
        }
    }
    private static Receipt Describe(Attempt attempt, string outcome) => new(attempt.Sequence,
        attempt.State.Id, attempt.State.ViewerId, attempt.State.Revision, attempt.State.BoardRevision, attempt.State.Decision?.Id,
        attempt.Command.GetType().Name, outcome, attempt.Retirement, attempt.DispatchAttempt, attempt.RetirementDiagnostic);
    private void Fail(string reason) => failure ??= reason;
    private static ClientDiagnosticCommand Kind(GameCommand command) => command switch {
        PassPriority => ClientDiagnosticCommand.PassPriority,
        KeepOpeningHand => ClientDiagnosticCommand.KeepOpeningHand,
        MulliganOpeningHand => ClientDiagnosticCommand.MulliganOpeningHand,
        PayMana => ClientDiagnosticCommand.PayMana,
        CancelPayment => ClientDiagnosticCommand.CancelPayment,
        UseOfferedAction => ClientDiagnosticCommand.UseOfferedAction,
        CompleteAttackers => ClientDiagnosticCommand.CompleteAttackers,
        CompleteBlockers => ClientDiagnosticCommand.CompleteBlockers,
        CompleteSelection => ClientDiagnosticCommand.CompleteSelection,
        CancelSelection => ClientDiagnosticCommand.CancelSelection,
        AcceptConfirmation => ClientDiagnosticCommand.AcceptConfirmation,
        DeclineConfirmation => ClientDiagnosticCommand.DeclineConfirmation,
        AcknowledgeDecision => ClientDiagnosticCommand.AcknowledgeDecision,
        AcknowledgeReveal => ClientDiagnosticCommand.AcknowledgeReveal,
        ChooseOptions => ClientDiagnosticCommand.ChooseOptions,
        SelectPlayer => ClientDiagnosticCommand.SelectPlayer,
        SelectCard => ClientDiagnosticCommand.SelectCard,
        _ => throw new InvalidDataException("The polled owner has no typed ledger kind for " + command.GetType().Name)
    };
}
