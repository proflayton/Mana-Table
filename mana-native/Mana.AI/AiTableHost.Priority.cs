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
        internal long CertifiedPasses, AwaitingSince;
        internal bool GrantAccepted, AwaitingGrant, AwaitingAcknowledgement, AwaitingWithdrawal, WithdrawalAccepted;
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
            owner.Sequence = Math.Max(owner.Sequence, frame.Sequence);
            if (owner.GrantAccepted && frame.GrantId == owner.GrantId) {
                if (frame.AutomaticPasses < owner.ObservedGrantPasses)
                    throw new InvalidDataException("An automated priority certificate rolled back its accepted pass count.");
                owner.CertifiedPasses += frame.AutomaticPasses - owner.ObservedGrantPasses;
                owner.ObservedGrantPasses = frame.AutomaticPasses; owner.AwaitingGrant = false;
            }
            if (owner.AwaitingWithdrawal) {
                if (frame.Sequence >= owner.WithdrawThrough && frame.Status is not
                    (PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)) {
                    owner.AwaitingWithdrawal = false; owner.GrantAccepted = false; owner.AwaitingGrant = false;
                } else { CheckPriorityProgress(owner); return PriorityWork.Reserved; }
            }
            if (owner.AwaitingAcknowledgement) {
                if (credit.Registered && credit.AcknowledgedBoardRevision >= owner.AcknowledgedBoard
                    && credit.AcknowledgedPublicActivitySequence >= owner.AcknowledgedActivity)
                    owner.AwaitingAcknowledgement = false;
                else { CheckPriorityProgress(owner); return PriorityWork.Reserved; }
            }
            if (owner.AwaitingGrant) { CheckPriorityProgress(owner); return PriorityWork.Reserved; }

            bool active = frame.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held;
            bool optedIn = session.PriorityAutomation == AiPriorityAutomation.NoResponse;
            bool playable = (state.Status is "playing" or "resolving") && state.Viewer is { Eliminated: false };
            bool paced = pacing == null || pacing.Ready(state.Id, observation.SeatId);
            bool signed = state.StackInterruptionVersion is >= 0;
            if (active && (!optedIn || !playable || !paced || credit.Overflowed || owner.Legacy
                || !signed || !owner.GrantAccepted || frame.GrantId != owner.GrantId))
                return await BeginPriorityWithdrawalAsync(owner, frame.Sequence, controls, cancellation).ConfigureAwait(false);
            if (!playable) return PriorityWork.Policy;
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
            if ((active || priority) && (!credit.Registered
                || owner.AcknowledgedStackVersion != state.StackInterruptionVersion
                || credit.PublicActivitySequence - credit.AcknowledgedPublicActivitySequence >= 16)) {
                var acknowledgement = new AutomatedPriorityControl.Acknowledge(state.Id, credit.BoardRevision, credit.PublicActivitySequence);
                var result = await SendPriorityControlAsync(owner, controls, acknowledgement, cancellation).ConfigureAwait(false);
                if (result == AutomatedPriorityControlResult.Accepted) {
                    owner.Acknowledgements++; owner.AcknowledgedBoard = credit.BoardRevision;
                    owner.AcknowledgedActivity = credit.PublicActivitySequence;
                    owner.AcknowledgedStackVersion = state.StackInterruptionVersion;
                    owner.AwaitingAcknowledgement = true;
                    owner.AwaitingSince = operationTime.GetTimestamp();
                }
                return result == AutomatedPriorityControlResult.Accepted ? PriorityWork.Refreshed : PriorityWork.Reserved;
            }
            if (active && frame.ReasonCode is "awaiting-observation" or "observation-overflow")
                return PriorityWork.Reserved;
            if (active) return PriorityWork.Reserved;
            if (!priority || owner.AttemptRevision == state.Revision) return PriorityWork.Policy;
            if (owner.Sequence == long.MaxValue) throw new InvalidDataException("Automated priority sequence exhausted.");
            owner.AttemptRevision = state.Revision;
            owner.Sequence++; owner.GrantSequence = owner.Sequence; owner.GrantId = Guid.NewGuid().ToString("N");
            owner.ObservedGrantPasses = 0;
            var grant = new AutomatedPriorityControl.NoResponse(state.Id, state.Decision!.Id,
                owner.Sequence, owner.GrantId, []);
            // Retain the negative fence before dispatch: cancellation cannot
            // prove that an affirmative command failed to reach the engine.
            var accepted = await SendPriorityControlAsync(owner, controls, grant, cancellation).ConfigureAwait(false);
            if (accepted == AutomatedPriorityControlResult.Accepted) {
                owner.Grants++; owner.GrantAccepted = true; owner.AwaitingGrant = true;
                owner.AwaitingSince = operationTime.GetTimestamp();
            }
            return accepted == AutomatedPriorityControlResult.Accepted ? PriorityWork.Refreshed : PriorityWork.Reserved;
        } finally { PublishPriorityEvidence(); }
    }

    private void CheckPriorityProgress(PriorityOwner owner)
    {
        if (operationTime.GetElapsedTime(owner.AwaitingSince) >= ObservationTimeout)
            throw new TimeoutException("The automated priority control did not acquire a fresh observed acknowledgement.");
    }

    private async Task<AutomatedPriorityControlResult> SendPriorityControlAsync(PriorityOwner owner,
        IAutomatedPriorityContinuation controls, AutomatedPriorityControl control, CancellationToken cancellation)
    {
        var result = await AwaitProvider("priority-control", token => controls.ExecuteAutomatedPriorityControlAsync(owner.Epoch,
            owner.Seat, control, token), cancellation).ConfigureAwait(false);
        if (!Enum.IsDefined(result)) throw new InvalidDataException("Unknown automated priority receipt.");
        return controls.AutomatedPriorityEpoch == owner.Epoch ? result : AutomatedPriorityControlResult.Retired;
    }

    private async Task<PriorityWork> BeginPriorityWithdrawalAsync(PriorityOwner owner, long through,
        IAutomatedPriorityContinuation controls, CancellationToken cancellation)
    {
        owner.WithdrawThrough = Math.Max(Math.Max(through, owner.GrantSequence), owner.WithdrawThrough);
        if (owner.WithdrawThrough <= 0) return PriorityWork.Policy;
        var result = await SendPriorityControlAsync(owner, controls,
            new AutomatedPriorityControl.Withdraw(owner.Game, owner.WithdrawThrough), cancellation).ConfigureAwait(false);
        owner.AwaitingWithdrawal = true; owner.WithdrawalAccepted = result == AutomatedPriorityControlResult.Accepted;
        owner.AwaitingSince = operationTime.GetTimestamp();
        if (owner.WithdrawalAccepted) owner.Withdrawals++;
        return result == AutomatedPriorityControlResult.Accepted ? PriorityWork.Refreshed : PriorityWork.Reserved;
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
        if (result == AutomatedPriorityControlResult.Accepted) {
            owner.Withdrawals++; owner.WithdrawalAccepted = true;
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
