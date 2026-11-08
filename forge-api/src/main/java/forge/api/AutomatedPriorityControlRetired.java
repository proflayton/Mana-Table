package forge.api;

/** Known pre-mutation retirement of an automated metadata scope. This is never
 * a translation for a scan, publication or command failure after admission. */
final class AutomatedPriorityControlRetired extends forge.api.sync.SyncTransport.CodedFailure {
    static final String CODE = "automated-priority-control-retired";

    AutomatedPriorityControlRetired() {
        super(CODE, "The automated priority control scope is no longer current.");
    }
}
