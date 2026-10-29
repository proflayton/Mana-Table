using Mana.Contracts;

namespace Mana.Client;

public sealed partial class ClientSession
{
    private sealed class PendingFiniteRevoke(RevokeFiniteStackYield command, int viewerId, long minimumRevision)
    {
        internal RevokeFiniteStackYield Command { get; } = command;
        internal int ViewerId { get; } = viewerId;
        internal long MinimumRevision = minimumRevision;
        internal bool Sent;
        internal DateTimeOffset Started;
    }
    // A negative capability survives resetting the presentation replica. It
    // carries only the exact grant to revoke, never an old gameplay snapshot.
    private readonly Dictionary<(string Game, string Grant), PendingFiniteRevoke> finiteRevocations = [];
    private sealed record AcknowledgedFiniteRevoke(string Game, string Grant, int Viewer, long Revision);
    private sealed class FiniteYieldOwnershipException : IOException
    {
        internal FiniteYieldOwnershipException() : base("An active finite-yield grant contradicts its observed revocation acknowledgement.") { }
    }
    // At most one acknowledgement: only the grant still advertised by the
    // retained replica needs suppression until that replica is replaced.
    private AcknowledgedFiniteRevoke? acknowledgedFiniteRevoke;

    private void QueueFiniteRevocation()
    {
        if (finiteIntent is FiniteResolutionIntent finite && finite.CaptureServerRevocation() is { } owned)
            QueueFiniteRevocation(owned.Command, owned.ViewerId, owned.MinimumRevision);
        if (State?.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } observed)
            QueueFiniteRevocation(new(State.Id, observed.GrantId), State.ViewerId, State.Revision);
    }
    private void QueueFiniteRevocation(RevokeFiniteStackYield command, int viewerId, long minimumRevision)
    {
        lock (observationGate) {
            if (acknowledgedFiniteRevoke is { } ack && ack.Game == command.GameId && ack.Grant == command.GrantId && ack.Viewer == viewerId) return;
            var key = (command.GameId, command.GrantId);
            if (!finiteRevocations.TryGetValue(key, out var pending)) finiteRevocations.Add(key, new(command, viewerId, minimumRevision));
            else if (pending.ViewerId == viewerId) pending.MinimumRevision = Math.Max(pending.MinimumRevision, minimumRevision);
        }
    }
    private void RecordFiniteRevocation(GameSnapshot receipt, RevokeFiniteStackYield command)
    {
        if (State is { FiniteStackYield.Status: FiniteStackYieldStatus.Active } retained
            && retained.Id == command.GameId && retained.ViewerId == receipt.ViewerId
            && retained.FiniteStackYield.GrantId == command.GrantId)
            acknowledgedFiniteRevoke = new(command.GameId, command.GrantId, receipt.ViewerId, receipt.Revision);
    }
    private void ValidateFiniteAcknowledgement(GameSnapshot? state)
    {
        if (acknowledgedFiniteRevoke is { } ack && state is { FiniteStackYield.Status: FiniteStackYieldStatus.Active }
            && state.Id == ack.Game && state.ViewerId == ack.Viewer && state.FiniteStackYield.GrantId == ack.Grant
            && state.Revision >= ack.Revision) throw new FiniteYieldOwnershipException();
    }
    private void PruneFiniteAcknowledgement(GameSnapshot? state)
    {
        if (acknowledgedFiniteRevoke is { } ack
            && (state == null || state.Id != ack.Game || state.ViewerId != ack.Viewer || state.FiniteStackYield?.GrantId != ack.Grant))
            acknowledgedFiniteRevoke = null;
    }
    private void RecoverRetiredFiniteCleanup(long epoch)
    {
        if (epoch != replica.Epoch || State != null || finiteRevocations.Count != 0
            || !recoveryRequired || !UncertainCommand || !LastFailureWasCommand) return;
        // Explicit work observed every outstanding negative acknowledgement.
        // Retired gameplay stays retired; no snapshot or affirmative input is
        // recovered, and a live game's uncertainty cannot use this path.
        recoveryRequired = UncertainCommand = LastFailureWasCommand = false;
        LastConnectionError = "";
        Recovered?.Invoke();
    }
    private void ReconcileFiniteYield(GameSnapshot? state, bool recovered)
    {
        if (state?.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } observed
            && (finiteIntent is not FiniteResolutionIntent finite || !finite.OwnsServerGrant(observed)
                || !finite.IsActive || finite.IsPaused))
            QueueFiniteRevocation(new(state.Id, observed.GrantId), state.ViewerId, state.Revision);
        if (!recovered || state == null) return;
        // An uncertain send is never retried blind. Only a fresh recovered
        // advertisement of that exact still-active grant can justify resending
        // its idempotent negative command.
        foreach (var pending in finiteRevocations.Values.Where(item => item.Command.GameId == state.Id && item.ViewerId == state.ViewerId))
            if (state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } grant
                && grant.GrantId == pending.Command.GrantId) { pending.Sent = false; pending.Started = default; }
    }

    private void DrainFiniteRevocation()
    {
        if (Busy || Polling || Closed || NeedsRecovery || finiteRevocations.Count == 0) return;
        var pending = finiteRevocations.Values.First();
        Run(async epoch => {
            var next = await ObserveFiniteRevocationAsync(pending);
            bool remains = RemainsActive(next, pending.Command);
            if (remains && time.GetUtcNow() - pending.Started >= TimeSpan.FromSeconds(5))
                throw new IOException("The finite-yield revocation has not completed.");
            Post(() => {
                if (epoch != replica.Epoch) return;
                if (!remains) {
                    RecordFiniteRevocation(next, pending.Command);
                    if (finiteIntent is FiniteResolutionIntent finite) finite.AcknowledgeServerRevocation(next, pending.Command);
                    finiteRevocations.Remove((pending.Command.GameId, pending.Command.GrantId));
                }
                // Reset/close must never restore a historical game just to
                // acknowledge withdrawing its consent.
                if (State is { } current && current.Id == next.Id && current.ViewerId == next.ViewerId)
                    Accept(next, epoch);
            });
        });
    }

    private async Task<GameSnapshot> ObserveFiniteRevocationAsync(PendingFiniteRevoke pending)
    {
        if (!pending.Sent) {
            pending.Sent = true; pending.Started = time.GetUtcNow();
            try { await connection.ExecuteAsync(pending.Command); }
            catch (EngineCommandRejectedException) {
                // A prior grant may already be terminal/replaced. Rejection
                // itself proves nothing; reconcile the actual advertisement.
            }
        }
        var next = await connection.ObserveAsync()
            ?? throw new IOException("The finite-yield revocation could not be observed.");
        // A transport return is not an acknowledgement until the same typed
        // evidence required by ordinary session acceptance has been validated.
        lock (observationGate) {
            executions.Validate(next); FiniteResolutionFacts.Validate(next); FiniteStackYieldFacts.Validate(next);
            if (next.Id != pending.Command.GameId || next.ViewerId != pending.ViewerId || next.FiniteStackYield == null
                || next.Revision < pending.MinimumRevision
                || State is { } current && current.Id == next.Id && current.ViewerId == next.ViewerId && next.Revision < current.Revision)
                throw new IOException("The finite-yield revocation receipt does not identify its current captured audience.");
            ValidateFiniteAcknowledgement(next);
            if (next.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } active && active.GrantId != pending.Command.GrantId)
                throw new IOException("Finite-yield ownership changed before revocation could be acknowledged.");
        }
        return next;
    }
    private static bool RemainsActive(GameSnapshot state, RevokeFiniteStackYield command)
        => state.Id == command.GameId && state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } grant
            && grant.GrantId == command.GrantId;

    private async Task RevokeAtCloseAsync(Task prior, IReadOnlyList<PendingFiniteRevoke> pending)
    {
        // The old command has already been sent: finish its Execute/Observe
        // before withdrawing consent on the same connection. No second lane.
        await prior;
        foreach (var item in pending) {
            try {
                if (item.Sent) {
                    // An earlier send may have committed without its ACK.
                    // Reobserve first; retry only its exact still-active grant.
                    var observed = await ObserveFiniteRevocationAsync(item);
                    if (!RemainsActive(observed, item.Command)) continue;
                    item.Sent = false;
                }
                var next = await ObserveFiniteRevocationAsync(item);
                if (RemainsActive(next, item.Command))
                    throw new IOException("The finite-yield revocation was not acknowledged before closing.");
            }
            catch (Exception error) { LastConnectionError = error.Message; }
        }
        // Best effort shutdown is not acknowledgement. The provider also owns
        // clearing the seat's grant when its transport generation disconnects.
    }
}
