package forge.api;

/** A private read lost its input/version ownership; it grants no action authority. */
final class StaleObservationException extends RuntimeException {
    static final String CODE = "observation-superseded";

    StaleObservationException() {
        super("The automated input changed during discovery");
    }
}
