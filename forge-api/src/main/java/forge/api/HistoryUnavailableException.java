package forge.api;

/** Optional feature absence established by the seat transport handshake. */
final class HistoryUnavailableException extends UnsupportedOperationException {
    static final String CODE = "history-unavailable";
    HistoryUnavailableException() { super("This table does not advertise match history"); }
}
