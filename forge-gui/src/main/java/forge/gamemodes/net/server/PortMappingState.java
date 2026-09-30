package forge.gamemodes.net.server;

/** A mapping result belongs to one hosting attempt, including late gateway replies. */
public final class PortMappingState {
    private long generation;
    private String status = "disabled";

    public synchronized long begin() {
        status = "searching";
        return ++generation;
    }

    public synchronized boolean complete(long attempt, boolean success) {
        if (attempt != generation || status.equals("disabled")) return false;
        // Another gateway failing must not undo an established mapping. A late
        // success after the discovery timeout is still a real mapping.
        if (!success && status.equals("mapped")) return false;
        status = success ? "mapped" : "failed";
        return true;
    }

    public synchronized void reset() {
        generation++;
        status = "disabled";
    }

    public synchronized String status() { return status; }
}
