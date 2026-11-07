using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public sealed partial class ClientSession
{
    private sealed record PriorityIntent(string Game, int Viewer, PriorityContinuationMode Mode,
        ImmutableArray<string> Stops, string QuietToken);
    private sealed record PriorityInstruction(GrantPriorityContinuation Command, int Viewer, long MinimumRevision);
    private sealed class PendingPriorityRevoke(RevokePriorityContinuation command, int viewer, long minimumRevision)
    {
        internal RevokePriorityContinuation Command = command;
        internal int Viewer = viewer;
        internal long MinimumRevision = minimumRevision;
        internal bool Sent;
        internal DateTimeOffset Started;
    }
    private PriorityIntent? priorityIntent;
    private PriorityInstruction? priorityInstruction;
    private long prioritySequence;
    // Negative consent survives presentation retirement, like finite consent.
    // The ordinary session Run lane owns every Execute/Observe exchange.
    private readonly Dictionary<(string Game, int Viewer), PendingPriorityRevoke> priorityRevocations = [];
    private readonly Dictionary<(string Game, int Viewer), (long Through, long Revision)> priorityAcknowledgements = [];
    public bool SupportsPriorityContinuation => Authority == ClientSessionAuthority.Interactive
        && connection.SupportsPriorityContinuation && State?.PriorityContinuation != null;
    public bool PriorityContinuationReserved => priorityIntent != null || priorityInstruction != null || priorityRevocations.Count > 0;
    public bool PriorityContinuationPending => priorityIntent != null && priorityInstruction == null
        || priorityInstruction is { } instruction && State?.PriorityContinuation?.GrantId != instruction.Command.GrantId;
    public bool PriorityContinuationWithdrawing => priorityRevocations.Count > 0;

    /// <summary>Retains one explicit policy, never a sequence of client passes.
    /// Quiet retains its original unchanged-window token. Returns true when this
    /// session owns dispatch, including waiting for a finite withdrawal ACK.</summary>
    public bool EnsurePriorityContinuation(PriorityContinuationMode mode, ImmutableArray<string> phaseStops, string quietYieldToken = "")
    {
        if (!SupportsPriorityContinuation || Closed || NeedsRecovery || State is not { Status: "playing", Viewer: { Eliminated: false } } state)
            return false;
        if (pendingManualCommand != null) return true;
        if (!Enum.IsDefined(mode) || !PriorityContinuationFacts.Stops(phaseStops)
            || (mode == PriorityContinuationMode.Quiet ? !PriorityContinuationFacts.Identifier(quietYieldToken) : quietYieldToken != ""))
            throw new ArgumentException("A priority policy requires closed own-turn stops and its original quiet token.");
        var desired = new PriorityIntent(state.Id, state.ViewerId, mode, phaseStops, quietYieldToken);
        if (priorityIntent is { } prior && prior.Game == desired.Game && prior.Viewer == desired.Viewer
            && prior.Mode == mode && prior.QuietToken == quietYieldToken && prior.Stops.SequenceEqual(phaseStops)) return true;
        RevokePriorityContinuation();
        StopFiniteIntent(); advancing?.Cancel();
        priorityIntent = desired;
        DrainPriorityContinuation();
        return true;
    }

    public void RevokePriorityContinuation()
    {
        if (Authority != ClientSessionAuthority.Interactive) return;
        lock (observationGate) {
            priorityIntent = null;
            if (priorityInstruction is { } instruction)
                QueuePriorityRevocation(instruction.Command.GameId, instruction.Viewer, instruction.Command.Sequence, instruction.MinimumRevision);
            if (State is { PriorityContinuation: { Status: PriorityContinuationStatus.Active or PriorityContinuationStatus.Held } grant } current)
                QueuePriorityRevocation(current.Id, current.ViewerId, grant.Sequence, current.Revision);
            priorityInstruction = null;
        }
    }

    private void QueuePriorityRevocation(string game, int viewer, long through, long revision)
    {
        if (through <= 0 || priorityAcknowledgements.TryGetValue((game, viewer), out var ack) && ack.Through >= through) return;
        if (priorityRevocations.TryGetValue((game, viewer), out var prior)) {
            if (through > prior.Command.ThroughSequence) {
                prior.Command = new(game, through); prior.Sent = false; prior.Started = default;
            }
            prior.MinimumRevision = Math.Max(prior.MinimumRevision, revision);
        } else priorityRevocations.Add((game, viewer), new(new(game, through), viewer, revision));
    }

    private static bool PriorityRevoked(GameSnapshot state, PendingPriorityRevoke pending)
        => state.PriorityContinuation is { } value && value.Sequence >= pending.Command.ThroughSequence
            && (value.Status is not (PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)
                || value.Sequence > pending.Command.ThroughSequence);

    private void ReconcilePriorityContinuation(GameSnapshot? state, bool recovered)
    {
        if (state == null) { RevokePriorityContinuation(); return; }
        if (state.PriorityContinuation is not { } grant) return;
        prioritySequence = Math.Max(prioritySequence, grant.Sequence);
        if (grant.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held
            && (priorityInstruction == null || priorityIntent == null || priorityInstruction.Command.GrantId != grant.GrantId))
            QueuePriorityRevocation(state.Id, state.ViewerId, grant.Sequence, state.Revision);
        foreach (var pending in priorityRevocations.Values.Where(item => item.Command.GameId == state.Id && item.Viewer == state.ViewerId).ToArray()) {
            if (PriorityRevoked(state, pending) && state.Revision >= pending.MinimumRevision) RecordPriorityRevocation(state, pending);
            else if (recovered) { pending.Sent = false; pending.Started = default; }
        }
    }

    private void ValidatePriorityAcknowledgement(GameSnapshot? state)
    {
        if (state?.PriorityContinuation is { Status: PriorityContinuationStatus.Active or PriorityContinuationStatus.Held } grant
            && priorityAcknowledgements.TryGetValue((state.Id, state.ViewerId), out var ack)
            && grant.Sequence <= ack.Through && state.Revision >= ack.Revision)
            throw new IOException("A priority continuation contradicts its acknowledged withdrawal fence.");
    }

    private void RecordPriorityRevocation(GameSnapshot state, PendingPriorityRevoke pending)
    {
        priorityAcknowledgements[(pending.Command.GameId, pending.Viewer)] = (pending.Command.ThroughSequence, state.Revision);
        priorityRevocations.Remove((pending.Command.GameId, pending.Viewer));
    }

    private void DrainPriorityContinuation()
    {
        if (Busy || Polling || Closed || NeedsRecovery) return;
        if (priorityRevocations.Values.FirstOrDefault() is { } pending) {
            Run(async epoch => {
                var next = await ObservePriorityRevocationAsync(pending);
                if (!PriorityRevoked(next, pending) && time.GetUtcNow() - pending.Started >= TimeSpan.FromSeconds(5))
                    throw new IOException("Priority withdrawal has not completed.");
                Post(() => {
                    if (epoch != replica.Epoch) return;
                    if (PriorityRevoked(next, pending)) RecordPriorityRevocation(next, pending);
                    if (State is { } current && current.Id == next.Id && current.ViewerId == next.ViewerId)
                        Accept(next, epoch);
                });
            });
            return;
        }
        if (priorityIntent is not { } intent || priorityInstruction != null || finiteRevocations.Count > 0 || pendingManualCommand != null
            || State is not { PriorityContinuation: { } prior, Decision: { } decision } state
            || state.Id != intent.Game || state.ViewerId != intent.Viewer) return;
        long sequence = checked(Math.Max(prioritySequence, prior.Sequence) + 1);
        var grant = new GrantPriorityContinuation(state.Id, decision.Id, sequence, Guid.NewGuid().ToString("N"), intent.Mode, intent.Stops, intent.QuietToken);
        if (!GameCommandPermissions.Allows(state, grant)) return;
        prioritySequence = sequence;
        priorityInstruction = new(grant, state.ViewerId, state.Revision);
        Run(async epoch => {
            var exchange = new CommandExchange(connection, diagnostics);
            var attempt = diagnostics.Begin(state, grant, ClientDiagnosticOrigin.Direct, epoch);
            await exchange.ExecuteAsync(grant, attempt);
            var next = await exchange.ObserveAsync(state, attempt, validate: next => {
                if (next?.PriorityContinuation is not { } accepted || next.ViewerId != state.ViewerId
                    || accepted.Sequence < grant.Sequence || accepted.GrantId != grant.GrantId
                    || accepted.Mode != grant.Mode || !accepted.PhaseStops.SequenceEqual(grant.PhaseStops))
                    throw new IOException("The priority consent receipt does not identify its captured instruction.");
            });
            var phase = exchange.ObservationFailurePhase;
            Post(() => Accept(next, epoch, commandObservation: true, observationFailurePhase: phase));
        });
    }

    private async Task<GameSnapshot> ObservePriorityRevocationAsync(PendingPriorityRevoke pending)
    {
        var exchange = new CommandExchange(connection, commandSubmitted: pending.Sent);
        if (!pending.Sent) {
            pending.Sent = true; pending.Started = time.GetUtcNow();
            try { await exchange.ExecuteAsync(pending.Command); }
            catch (CommandExchangeException error) when (error.Phase == CommandFailurePhase.DispatchRejected) { }
        }
        return (await exchange.ObserveAsync(validate: next => {
            if (next == null) throw new IOException("Priority withdrawal could not be observed.");
            lock (observationGate) {
                executions.Validate(next); FiniteStackYieldFacts.Validate(next); PriorityContinuationFacts.Validate(next);
                if (next.Id != pending.Command.GameId || next.ViewerId != pending.Viewer || next.PriorityContinuation == null
                    || next.Revision < pending.MinimumRevision
                    || State is { } current && current.Id == next.Id && current.ViewerId == next.ViewerId && next.Revision < current.Revision)
                    throw new IOException("Priority withdrawal does not identify its captured current audience.");
                ValidatePriorityAcknowledgement(next);
            }
        }))!;
    }

    private async Task RevokePriorityAtCloseAsync(Task prior, IReadOnlyList<PendingPriorityRevoke> pending)
    {
        await prior;
        foreach (var item in pending) {
            try {
                if (item.Sent) {
                    var observed = await ObservePriorityRevocationAsync(item);
                    if (PriorityRevoked(observed, item)) continue;
                    item.Sent = false;
                }
                if (!PriorityRevoked(await ObservePriorityRevocationAsync(item), item))
                    throw new IOException("Priority withdrawal was not acknowledged before closing.");
            } catch (Exception error) { LastConnectionError = error.Message; }
        }
    }
}
