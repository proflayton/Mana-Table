using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public sealed record AiAutomatedPriorityEvidence(string GameId, int SeatId, long Epoch,
    int AcceptedAcknowledgements, int AcceptedGrants, int AcceptedWithdrawals, long CertifiedAutomaticPasses,
    long LastSequence, long LastAcknowledgedBoardRevision, long LastAcknowledgedPublicActivitySequence, bool Retired);

public sealed partial class AiTableHost
{
    private enum PriorityWork { Policy, Reserved, Refreshed }
    private sealed class PriorityEvidenceOverflowException : Exception { }
    private sealed class PriorityOwner(string game, int seat, long epoch)
    {
        internal readonly string Game = game;
        internal readonly int Seat = seat;
        internal readonly long Epoch = epoch;
        internal long Sequence, GrantSequence, AcknowledgedBoard = -1, AcknowledgedActivity = -1;
        internal long? AcknowledgedStackVersion;
        internal string GrantId = "";
        internal int Acknowledgements, Grants, Withdrawals, ObservedGrantPasses;
        internal long CertifiedPasses;
        internal bool GrantAccepted, WithdrawalAccepted;
        internal bool Retired, Legacy;
        internal long WithdrawThrough, AttemptRevision = -1;
        internal string ManualDecision = "";
        internal AiAutomatedPriorityEvidence Evidence => new(Game, Seat, Epoch, Acknowledgements, Grants, Withdrawals,
            CertifiedPasses, Sequence, AcknowledgedBoard, AcknowledgedActivity, Retired);
    }
    private readonly Dictionary<(string Game, int Seat, long Epoch), PriorityOwner> priorityOwners = [];
    private AiAutomatedPriorityEvidence[] priorityEvidence = [];
    public ImmutableArray<AiAutomatedPriorityEvidence> PriorityContinuations => Volatile.Read(ref priorityEvidence).ToImmutableArray();
    private void PublishPriorityEvidence() => Volatile.Write(ref priorityEvidence,
        priorityOwners.Values.Select(owner => owner.Evidence).ToArray());

    private async Task<PriorityWork> MaintainPriorityAsync(AutomatedSeatObservation observation, AiSeatSession session,
        CancellationToken cancellation)
    {
        if (provider is not IAutomatedPriorityContinuation { SupportsAutomatedPriorityContinuation: true } controls)
            return PriorityWork.Policy;
        var state = observation.State;
        long epoch = controls.AutomatedPriorityEpoch;
        if (epoch <= 0 || state.PriorityContinuation is not { } frame || state.PriorityObservation is not { } credit)
            return PriorityWork.Policy;
        var key = (state.Id, observation.SeatId, epoch);
        if (!priorityOwners.TryGetValue(key, out var owner)) {
            priorityOwners.Add(key, owner = new(state.Id, observation.SeatId, epoch));
            // Retired telemetry is a bounded diagnostic tail, not authority.
            foreach (var stale in priorityOwners.Where(item => item.Value.Retired).Select(item => item.Key)
                .Take(Math.Max(0, priorityOwners.Count - 64)).ToArray()) priorityOwners.Remove(stale);
        }
        try {
            if (owner.Retired) return PriorityWork.Reserved;
            owner.Sequence = Math.Max(owner.Sequence, frame.Sequence);
            if (owner.GrantAccepted && frame.GrantId == owner.GrantId) {
                if (frame.AutomaticPasses < owner.ObservedGrantPasses)
                    throw new InvalidDataException("An automated priority certificate rolled back its accepted pass count.");
                owner.CertifiedPasses += frame.AutomaticPasses - owner.ObservedGrantPasses;
                owner.ObservedGrantPasses = frame.AutomaticPasses;
            }

            bool active = frame.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held;
            bool optedIn = session.PriorityAutomation == AiPriorityAutomation.NoResponse;
            bool playable = (state.Status is "playing" or "resolving") && state.Viewer is { Eliminated: false };
            bool paced = pacing == null || pacing.Ready(state.Id, observation.SeatId);
            bool signed = state.StackInterruptionVersion is >= 0;
            if (active && (!optedIn || !playable || !paced || credit.Overflowed || owner.Legacy
                || !signed || !owner.GrantAccepted || frame.GrantId != owner.GrantId))
                return await BeginPriorityWithdrawalAsync(owner, frame.Sequence, controls, cancellation).ConfigureAwait(false);
            if (!playable) {
                // Losing the ability to act does not end a registered table
                // observer's delivery lifetime. Retire active consent first;
                // only another owned read may acknowledge the ongoing table.
                if ((state.Status is "playing" or "resolving") && state.Viewer is { Eliminated: true }
                    && credit.Registered && !credit.Overflowed && signed && paced
                    && NeedsPriorityAcknowledgement(owner, state, credit)) {
                    var result = await AcknowledgePriorityAsync(owner, state, credit, controls, cancellation).ConfigureAwait(false);
                    return result is AutomatedPriorityControlResult.Accepted ? PriorityWork.Refreshed : PriorityWork.Reserved;
                }
                return PriorityWork.Policy;
            }
            if (!paced) return PriorityWork.Reserved;
            if (optedIn && credit.Overflowed) throw new PriorityEvidenceOverflowException();
            if (!optedIn || owner.Legacy) return PriorityWork.Policy;
            if (!signed) throw new InvalidDataException("An automated priority frame omitted its published action signature.");

            // A legacy human without observation support cannot be held forever
            // by this optional automation. An exhausted registered reading credit
            // is different: it must wait, never become a manual-pass bypass.
            if (active && frame.ReasonCode == "awaiting-observation-registration") {
                owner.Legacy = true;
                return await BeginPriorityWithdrawalAsync(owner, frame.Sequence, controls, cancellation).ConfigureAwait(false);
            }
            if (state.Decision is { } manual && active
                && (manual.Payload is not PriorityPayload || frame.Status == PriorityContinuationStatus.Held
                    && frame.ReasonCode is "response-available" or "response-unknown" or "phase-stop")) {
                owner.ManualDecision = manual.Id;
                return await BeginPriorityWithdrawalAsync(owner, frame.Sequence, controls, cancellation).ConfigureAwait(false);
            }
            if (owner.ManualDecision.Length > 0) {
                if (state.Decision?.Id == owner.ManualDecision) return PriorityWork.Policy;
                owner.ManualDecision = "";
            }
            bool priority = state.Status == "playing" && state.Turn > 0
                && state.Decision?.Payload is PriorityPayload { Pass.Enabled: true };
            bool canGrant = priority && state.Decision?.Payload is PriorityPayload {
                ResponseAvailability: PriorityResponseAvailability.Absent };
            if ((active || priority) && NeedsPriorityAcknowledgement(owner, state, credit)) {
                var result = await AcknowledgePriorityAsync(owner, state, credit, controls, cancellation).ConfigureAwait(false);
                if (result is AutomatedPriorityControlResult.Accepted accepted) {
                    // The receipt already confirms the ACK. Only an unchanged
                    // exact priority input can chain a grant, never a policy
                    // evaluation or a command against this earlier rich frame.
                    if (!active && canGrant && accepted.Receipt.SamePriorityInput(state)
                        && accepted.Receipt.Continuation.Status is not (PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)
                        && (pacing == null || pacing.Ready(state.Id, observation.SeatId)))
                        return await BeginPriorityGrantAsync(owner, state, controls, cancellation).ConfigureAwait(false);
                }
                return result is AutomatedPriorityControlResult.Accepted ? PriorityWork.Refreshed : PriorityWork.Reserved;
            }
            if (active && frame.ReasonCode is "awaiting-observation" or "observation-overflow")
                return PriorityWork.Reserved;
            if (active) return PriorityWork.Reserved;
            if (!canGrant || owner.AttemptRevision == state.Revision) return PriorityWork.Policy;
            return await BeginPriorityGrantAsync(owner, state, controls, cancellation).ConfigureAwait(false);
        } finally { PublishPriorityEvidence(); }
    }

    private static bool NeedsPriorityAcknowledgement(PriorityOwner owner, GameSnapshot state, PriorityObservationState credit)
        => !credit.Registered || owner.AcknowledgedStackVersion != state.StackInterruptionVersion
            || credit.PublicActivitySequence - credit.AcknowledgedPublicActivitySequence >= 16;

    private async Task<AutomatedPriorityControlResult> AcknowledgePriorityAsync(PriorityOwner owner, GameSnapshot state,
        PriorityObservationState credit, IAutomatedPriorityContinuation controls, CancellationToken cancellation)
    {
        var result = await SendPriorityControlAsync(owner, controls,
            new AutomatedPriorityControl.Acknowledge(state.Id, credit.BoardRevision, credit.PublicActivitySequence), cancellation).ConfigureAwait(false);
        if (result is AutomatedPriorityControlResult.Accepted) {
            owner.Acknowledgements++; owner.AcknowledgedBoard = credit.BoardRevision;
            owner.AcknowledgedActivity = credit.PublicActivitySequence;
            owner.AcknowledgedStackVersion = state.StackInterruptionVersion;
        }
        return result;
    }

    // At most ACK -> grant -> negative withdrawal in one observed cycle. Every
    // control path returns Reserved/Refreshed, so gameplay always starts after
    // another genuine owned rich observation, including after withdrawal.
    private async Task<PriorityWork> BeginPriorityGrantAsync(PriorityOwner owner, GameSnapshot state,
        IAutomatedPriorityContinuation controls, CancellationToken cancellation)
    {
        if (pacing != null && !pacing.Ready(state.Id, owner.Seat)) return PriorityWork.Reserved;
        if (owner.Sequence == long.MaxValue) throw new InvalidDataException("Automated priority sequence exhausted.");
        owner.AttemptRevision = state.Revision;
        owner.Sequence++; owner.GrantSequence = owner.Sequence; owner.GrantId = Guid.NewGuid().ToString("N");
        owner.ObservedGrantPasses = 0;
        var grant = new AutomatedPriorityControl.NoResponse(state.Id, state.Decision!.Id,
            owner.Sequence, owner.GrantId, []);
        // Retain the negative fence before dispatch: cancellation cannot prove
        // that an affirmative command failed to reach the engine.
        var result = await SendPriorityControlAsync(owner, controls, grant, cancellation).ConfigureAwait(false);
        if (result is not AutomatedPriorityControlResult.Accepted accepted) return PriorityWork.Reserved;
        owner.Grants++; owner.GrantAccepted = true; owner.WithdrawalAccepted = false;
        CaptureReceiptPasses(owner, accepted.Receipt);
        var receipt = accepted.Receipt;
        bool response = receipt.Continuation.Status == PriorityContinuationStatus.Held
            && (receipt.PriorityDecisionId == null || receipt.Continuation.ReasonCode is "response-available" or "response-unknown" or "phase-stop");
        if (response || pacing != null && !pacing.Ready(state.Id, owner.Seat)) {
            if (response) owner.ManualDecision = receipt.DecisionId ?? "";
            return await BeginPriorityWithdrawalAsync(owner, receipt.Continuation.Sequence, controls, cancellation).ConfigureAwait(false);
        }
        return PriorityWork.Refreshed;
    }

    private async Task<AutomatedPriorityControlResult> SendPriorityControlAsync(PriorityOwner owner,
        IAutomatedPriorityContinuation controls, AutomatedPriorityControl control, CancellationToken cancellation)
    {
        var result = await AwaitProvider("priority-control", token => controls.ExecuteAutomatedPriorityControlAsync(owner.Epoch,
            owner.Seat, control, token), cancellation).ConfigureAwait(false);
        if (controls.AutomatedPriorityEpoch != owner.Epoch) return new AutomatedPriorityControlResult.Retired();
        if (result is AutomatedPriorityControlResult.Retired) return result;
        if (result is not AutomatedPriorityControlResult.Accepted { Receipt: { } receipt })
            throw new InvalidDataException("Unknown automated priority receipt.");
        receipt.Validate();
        if (receipt.Epoch != owner.Epoch || receipt.GameId != owner.Game || receipt.SeatId != owner.Seat)
            throw new InvalidDataException("An automated priority receipt belongs to another owner.");
        bool confirmed = control switch {
            AutomatedPriorityControl.Acknowledge ack => receipt.Observation.Registered
                && receipt.Observation.AcknowledgedBoardRevision >= ack.BoardRevision
                && receipt.Observation.AcknowledgedPublicActivitySequence >= ack.PublicActivitySequence,
            AutomatedPriorityControl.NoResponse grant => receipt.Continuation.Sequence == grant.Sequence
                && receipt.Continuation.GrantId == grant.GrantId && receipt.Continuation.Mode == PriorityContinuationMode.NoResponse,
            AutomatedPriorityControl.Withdraw withdraw => receipt.Continuation.Sequence >= withdraw.ThroughSequence
                && (receipt.Continuation.Sequence > withdraw.ThroughSequence
                    || receipt.Continuation.Status is not (PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)),
            _ => false
        };
        if (!confirmed) throw new InvalidDataException("The automated priority receipt did not confirm its requested metadata.");
        owner.Sequence = Math.Max(owner.Sequence, receipt.Continuation.Sequence);
        return result;
    }

    private static void CaptureReceiptPasses(PriorityOwner owner, AutomatedPriorityReceipt receipt)
    {
        var frame = receipt.Continuation;
        if (!owner.GrantAccepted || frame.GrantId != owner.GrantId) return;
        if (frame.AutomaticPasses < owner.ObservedGrantPasses)
            throw new InvalidDataException("An automated priority certificate rolled back its accepted pass count.");
        owner.CertifiedPasses += frame.AutomaticPasses - owner.ObservedGrantPasses;
        owner.ObservedGrantPasses = frame.AutomaticPasses;
    }

    private async Task<PriorityWork> BeginPriorityWithdrawalAsync(PriorityOwner owner, long through,
        IAutomatedPriorityContinuation controls, CancellationToken cancellation)
    {
        owner.WithdrawThrough = Math.Max(Math.Max(through, owner.GrantSequence), owner.WithdrawThrough);
        if (owner.WithdrawThrough <= 0) return PriorityWork.Policy;
        var result = await SendPriorityControlAsync(owner, controls,
            new AutomatedPriorityControl.Withdraw(owner.Game, owner.WithdrawThrough), cancellation).ConfigureAwait(false);
        owner.WithdrawalAccepted = result is AutomatedPriorityControlResult.Accepted;
        if (result is AutomatedPriorityControlResult.Accepted accepted) {
            CaptureReceiptPasses(owner, accepted.Receipt);
            owner.Withdrawals++; owner.GrantAccepted = false;
        }
        return owner.WithdrawalAccepted ? PriorityWork.Refreshed : PriorityWork.Reserved;
    }

    private async Task RetirePriorityOwnersAsync(HashSet<(string Id, int SeatId)> active, CancellationToken cancellation)
    {
        if (provider is not IAutomatedPriorityContinuation controls) return;
        long epoch = controls.AutomatedPriorityEpoch;
        foreach (var owner in priorityOwners.Values.Where(owner => !owner.Retired
            && (owner.Epoch != epoch || !active.Contains((owner.Game, owner.Seat))))) {
            if (owner.Epoch == epoch && owner.GrantSequence > 0)
                await WithdrawPriorityOwnerAsync(owner, controls, cancellation).ConfigureAwait(false);
            owner.Retired = true;
        }
        PublishPriorityEvidence();
    }

    private async Task WithdrawPriorityOwnerAsync(PriorityOwner owner, IAutomatedPriorityContinuation controls,
        CancellationToken cancellation)
    {
        if (owner.Epoch != controls.AutomatedPriorityEpoch || owner.GrantSequence <= 0) return;
        if (owner.WithdrawalAccepted && owner.WithdrawThrough >= owner.GrantSequence) return;
        var result = await SendPriorityControlAsync(owner, controls,
            new AutomatedPriorityControl.Withdraw(owner.Game, Math.Max(owner.Sequence, owner.GrantSequence)), cancellation).ConfigureAwait(false);
        if (result is AutomatedPriorityControlResult.Accepted accepted) {
            CaptureReceiptPasses(owner, accepted.Receipt);
            owner.Withdrawals++; owner.WithdrawalAccepted = true;
            owner.GrantAccepted = false;
            owner.WithdrawThrough = Math.Max(owner.Sequence, owner.GrantSequence);
        }
    }

    private async Task WithdrawPriorityGameAsync(string? game, CancellationToken cancellation)
    {
        if (provider is not IAutomatedPriorityContinuation controls) return;
        try {
            foreach (var owner in priorityOwners.Values.Where(owner => !owner.Retired && (game == null || owner.Game == game))) {
                await WithdrawPriorityOwnerAsync(owner, controls, cancellation).ConfigureAwait(false);
                owner.Retired = true;
            }
        } finally { PublishPriorityEvidence(); }
    }
}
