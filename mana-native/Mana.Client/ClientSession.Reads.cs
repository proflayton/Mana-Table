using Mana.Contracts;

namespace Mana.Client;

public sealed partial class ClientSession
{
    // Owner-thread discovery, not an exclusive asynchronous lock. Capture and
    // source invocation cannot be separated by a caller's Pump or await.
    // Ordinary Poll remains independent and may retire the captured publication.
    // Outcomes describe only read ownership. Retired does not classify or clear
    // session recovery; callers must surface that health before using a result.
    public async Task<CurrentReadResult> ReadCurrentDecisionAsync(IOwnedDecisionObservations source,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellation.ThrowIfCancellationRequested();
        if (TryCaptureCurrentRead() is not { } read) return new CurrentReadResult.Deferred();
        OwnedDecisionObservation? observation;
        try { observation = await source.ObserveDecisionAsync(cancellation); }
        catch (EngineObservationSupersededException error) { return new CurrentReadResult.Retired(read, null, error); }
        // Polled owners apply already-posted completions before validating the
        // returned read. External producers retain their existing pump boundary.
        if (ObservationDelivery == ObservationDeliveryMode.Polled) Pump();
        return read.IsCurrent ? new CurrentReadResult.Current(read, observation)
            : new CurrentReadResult.Retired(read, observation, null);
    }

    public abstract class CurrentReadResult
    {
        private CurrentReadResult() { }
        public sealed class Deferred : CurrentReadResult
        {
            internal Deferred() { }
        }
        public sealed class Retired : CurrentReadResult
        {
            internal Retired(CurrentRead read, OwnedDecisionObservation? observation, EngineObservationSupersededException? superseded)
                => (Read, Observation, Superseded) = (read, observation, superseded);
            public CurrentRead Read { get; }
            public OwnedDecisionObservation? Observation { get; }
            public EngineObservationSupersededException? Superseded { get; }
        }
        public sealed class Current : CurrentReadResult
        {
            internal Current(CurrentRead read, OwnedDecisionObservation? observation) => (Read, Observation) = (read, observation);
            public CurrentRead Read { get; }
            public OwnedDecisionObservation? Observation { get; }
        }
    }

    private CurrentRead? TryCaptureCurrentRead()
    {
        lock (observationGate) return CurrentReadReady
            ? new(this, replica.Epoch, acceptedObservationVersion, operations.Generation, State) : null;
    }

    private bool CurrentReadReady => !Closed && !Polling && !NeedsRecovery
        && !operations.Occupied && operations.Pending == null
        && finiteRevocations.Count == 0 && priorityRevocations.Count == 0;

    /// <summary>A read-only publication stamp. It grants no command authority,
    /// never enters transport, and cannot be constructed for another session.</summary>
    public sealed class CurrentRead
    {
        private readonly ClientSession owner;
        private readonly long epoch, publication, operation;
        public GameSnapshot? Snapshot { get; }
        internal CurrentRead(ClientSession owner, long epoch, long publication, long operation, GameSnapshot? snapshot)
            => (this.owner, this.epoch, this.publication, this.operation, Snapshot) = (owner, epoch, publication, operation, snapshot);
        /// <summary>Only accepted publication advances this stamp. A returned
        /// or failed connection operation cannot supply fresh observation credit.</summary>
        public bool HasNewerPublication
        {
            get { lock (owner.observationGate) return owner.acceptedObservationVersion > publication; }
        }
        /// <summary>Connection lineage only: use together with current audience
        /// and complete payload checks. Independent polls may retain this lineage;
        /// this property does not grant current-publication or command authority.</summary>
        public bool HasSameOperation
        {
            get { lock (owner.observationGate) return !owner.Closed
                && owner.replica.Epoch == epoch && owner.operations.Generation == operation; }
        }
        public bool IsCurrent
        {
            get { lock (owner.observationGate) return owner.CurrentReadReady
                && owner.replica.Epoch == epoch && owner.acceptedObservationVersion == publication
                && owner.operations.Generation == operation
                && ReferenceEquals(owner.State, Snapshot); }
        }
    }
}
