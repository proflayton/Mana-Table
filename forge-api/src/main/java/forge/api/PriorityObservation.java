package forge.api;

import java.util.LinkedHashMap;
import java.util.Map;

/** Per-audience delivery certificates. Only an accepted, actually published
 * frame can replenish automatic progress; polling alone grants no credit. */
final class PriorityObservation {
    static final int MAXIMUM_LEAD = 64;
    private record Publication(long activity, long executions, long readSignature) { }
    private final Map<Long, Publication> publications = new LinkedHashMap<>();
    private long board, activity, acknowledgedBoard, acknowledgedActivity;
    private boolean initialized, registered;
    private long acknowledgedExecutions, acknowledgedReadSignature;

    record Frame(long boardRevision, long publicActivitySequence, long acknowledgedBoardRevision,
                 long acknowledgedPublicActivitySequence, boolean registered, boolean overflowed) { }

    void published(long revision, long sequence) {
        published(revision, sequence, 0);
    }
    void published(long revision, long sequence, long executions) {
        published(revision, sequence, executions, 0);
    }
    void published(long revision, long sequence, long executions, long readSignature) {
        initialized = true; board = revision; activity = sequence;
        publications.put(revision, new Publication(sequence, executions, readSignature));
        while (publications.size() > MatchActivity.MAXIMUM_FINITE_ENTRIES)
            publications.remove(publications.keySet().iterator().next());
    }

    boolean initialized() { return initialized; }
    boolean registered() { return registered; }
    long acknowledgedActivity() { return acknowledgedActivity; }

    long acknowledge(long revision, long sequence) {
        var actual = publications.get(revision);
        if (actual == null || actual.activity() != sequence || revision > board || sequence > activity)
            throw new IllegalArgumentException("That priority observation was not published to this seat");
        if (revision < acknowledgedBoard || sequence < acknowledgedActivity) return acknowledgedExecutions;
        registered = true; acknowledgedBoard = revision; acknowledgedActivity = sequence;
        acknowledgedExecutions = actual.executions();
        acknowledgedReadSignature = actual.readSignature();
        publications.entrySet().removeIf(entry -> entry.getKey() < acknowledgedBoard);
        return acknowledgedExecutions;
    }

    boolean hasCredit(long current) { return registered && current - acknowledgedActivity < MAXIMUM_LEAD; }
    boolean hasReadSignature(long current) { return registered && acknowledgedReadSignature == current; }
    Frame frame(boolean overflowed) {
        return new Frame(board, activity, acknowledgedBoard, acknowledgedActivity, registered, overflowed);
    }
    Frame projectedFrame(long revision, long sequence, boolean overflowed) {
        return new Frame(revision, sequence, acknowledgedBoard, acknowledgedActivity, registered, overflowed);
    }
    void disconnected() {
        publications.clear(); initialized = false; registered = false;
        board = 0; activity = 0; acknowledgedBoard = 0; acknowledgedActivity = 0;
        acknowledgedExecutions = 0; acknowledgedReadSignature = 0;
    }
}
