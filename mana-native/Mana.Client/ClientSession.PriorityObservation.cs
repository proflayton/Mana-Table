using Mana.Contracts;

namespace Mana.Client;

public sealed partial class ClientSession
{
    private bool priorityObservationReady = true;
    private long acceptedObservationVersion, priorityObservationRetryAfterVersion;
    private DateTimeOffset priorityObservationRetryAt;
    public bool PriorityObservationInFlight => operations.Active is { Quiet: true };

    /// <summary>Only accepted presentation can return reading credit. A game
    /// surface sets this false before accepting its first frame, then opens it
    /// after announcing and displaying the actual accepted activity.</summary>
    public void SetPriorityObservationReady(bool ready)
    {
        priorityObservationReady = ready;
        if (ready) DrainPriorityObservation();
    }

    private void DrainPriorityObservation()
    {
        if (Authority != ClientSessionAuthority.Interactive || !priorityObservationReady || operations.Active != null || Busy || Polling || Closed || NeedsRecovery
            || operations.Pending != null || finiteRevocations.Count > 0 || priorityRevocations.Count > 0
            || acceptedObservationVersion < priorityObservationRetryAfterVersion || time.GetUtcNow() < priorityObservationRetryAt
            || !connection.SupportsPriorityContinuation || State is not { PriorityObservation: { } observation, Viewer: { } } current
            || current.Status is not ("playing" or "resolving") || observation.Overflowed) return;
        bool waiting = current.PriorityContinuation?.ReasonCode is "awaiting-observation" or "awaiting-observation-registration";
        if (observation.Registered && observation.PublicActivitySequence <= observation.AcknowledgedPublicActivitySequence
            && observation.BoardRevision <= observation.AcknowledgedBoardRevision) return;
        if (observation.Registered && observation.PublicActivitySequence - observation.AcknowledgedPublicActivitySequence < 16 && !waiting) return;
        var command = new AcknowledgePriorityObservation(current.Id, observation.BoardRevision, observation.PublicActivitySequence);
        long epoch = replica.Epoch, accepted = acceptedObservationVersion;
        StartOwnedOperation(true, async context => {
            var exchange = new CommandExchange(connection, context);
            await exchange.ExecuteAsync(command);
            var next = await exchange.ObserveAsync(current);
            return new OwnerCompletion.CurrentOnlyAction(() => {
                if (State?.Id != current.Id || State.ViewerId != current.ViewerId) return;
                Accept(next, epoch, observationFailurePhase: ClientFailurePhase.Observation, failureContext: context);
            });
        }, (context, failure) => {
                if (State?.Id == current.Id && State.ViewerId == current.ViewerId) {
                    // Metadata is not a gameplay answer. A lost receipt cannot
                    // freeze controls or replay an affirmative command. Wait
                    // for a fresh accepted authority read and bounded retry.
                    priorityObservationRetryAfterVersion = accepted + 1;
                    priorityObservationRetryAt = time.GetUtcNow() + TimeSpan.FromSeconds(1);
                    RetirePendingForeground(ClientDiagnosticFailure.ObservationFailure);
                    CompleteRetiredForeground();
                }
        });
    }
}
