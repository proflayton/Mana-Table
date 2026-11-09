namespace Mana.Client;

public sealed partial class ClientSession
{
    // This is owner-thread admission, not an exclusive asynchronous lock.
    // Callers invoke their read immediately, without Pump/await between capture
    // and invocation, and do not concurrently pump this owner while awaiting it.
    // Ordinary Poll remains independent and may retire the captured publication.
    public CurrentRead? TryCaptureCurrentRead()
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
        public Mana.Contracts.GameSnapshot? Snapshot { get; }
        internal CurrentRead(ClientSession owner, long epoch, long publication, long operation, Mana.Contracts.GameSnapshot? snapshot)
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
