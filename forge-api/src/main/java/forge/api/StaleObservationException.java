package forge.api;

/** A private read lost its input/version ownership; it grants no action authority. */
final class StaleObservationException extends forge.api.sync.SyncTransport.CodedFailure {
    static final String CODE = "observation-superseded";

    StaleObservationException() {
        super(CODE, "The owned input changed during discovery");
    }
}
