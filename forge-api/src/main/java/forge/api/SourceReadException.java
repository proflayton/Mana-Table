package forge.api;

/** Method-scoped source-read stages; neither outcome grants action or retry
 * authority. Invalid access remains rejection; unexpected query failures retain
 * their original private causes inside an intentional boundary wrapper. */
final class SourceReadException extends forge.api.sync.SyncTransport.CodedFailure {
    static final String SUPERSEDED = "source-read-superseded", CHANGED = "source-read-changed", FAILED = "source-read-failed";
    static final String FAILED_MESSAGE = "The source action read failed.";
    enum Stage { PRE_READ, DISCOVERY, PREVIEW, FAILURE }
    private final Stage stage;

    private SourceReadException(String code, Stage stage, String message) {
        super(code, message); this.stage = stage;
    }
    static SourceReadException superseded() {
        return new SourceReadException(SUPERSEDED, Stage.PRE_READ, "The observed source frame was superseded before the read.");
    }
    static SourceReadException changed(Stage stage) {
        if (stage != Stage.DISCOVERY && stage != Stage.PREVIEW) throw new IllegalArgumentException("A changed read must have started discovery");
        return new SourceReadException(CHANGED, stage, stage == Stage.DISCOVERY
                ? "The source action read changed during discovery." : "The source action read changed during preview.");
    }
    static SourceReadException failed(Throwable cause) {
        var failure = new SourceReadException(FAILED, Stage.FAILURE, FAILED_MESSAGE);
        failure.initCause(cause); return failure;
    }
    static boolean isCode(String code) { return SUPERSEDED.equals(code) || CHANGED.equals(code) || FAILED.equals(code); }
    @Override public String toString() { return super.toString() + " [source-read stage=" + stage + "]"; }
}
