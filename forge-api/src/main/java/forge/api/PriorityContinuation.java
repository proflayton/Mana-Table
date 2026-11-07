package forge.api;

import com.google.gson.annotations.SerializedName;
import java.util.List;
import java.util.Set;

/** One connection's standing priority consent. No rules objects, action handles
 * or transport operations live here; MatchSession owns every actual pass. */
final class PriorityContinuation {
    enum Mode { @SerializedName("NoResponse") NO_RESPONSE, @SerializedName("Quiet") QUIET }
    enum Status { @SerializedName("Idle") IDLE, @SerializedName("Active") ACTIVE,
        @SerializedName("Held") HELD, @SerializedName("Revoked") REVOKED }
    static final Set<String> PHASES = Set.of("UPKEEP", "DRAW", "MAIN1", "COMBAT_BEGIN", "MAIN2", "END_OF_TURN");

    record Instruction(long sequence, String grantId, Mode mode, List<String> phaseStops, String quietYieldToken) {
        Instruction {
            if (sequence <= 0 || !identifier(grantId) || mode == null || phaseStops == null
                    || phaseStops.size() > PHASES.size() || phaseStops.stream().anyMatch(phase -> phase == null || !PHASES.contains(phase))
                    || Set.copyOf(phaseStops).size() != phaseStops.size()
                    || quietYieldToken == null || mode == Mode.QUIET && !identifier(quietYieldToken)
                    || mode == Mode.NO_RESPONSE && !quietYieldToken.isEmpty())
                throw new IllegalArgumentException("Invalid priority continuation instruction");
            phaseStops = List.copyOf(phaseStops);
        }
    }
    record Frame(long sequence, String grantId, Mode mode, Status status, String reasonCode,
                 int automaticPasses, List<String> phaseStops) { }

    private long sequence;
    private Instruction instruction;
    private Status status = Status.IDLE;
    private String reason = "";
    private int passes;

    long sequence() { return sequence; }
    Instruction instruction() { return instruction; }
    boolean accepted() { return instruction != null && (status == Status.ACTIVE || status == Status.HELD); }
    boolean current(Instruction expected) { return accepted() && instruction == expected && sequence == expected.sequence(); }

    /** Replays must be recognized before consulting the original, now retired
     * priority prompt. A withdrawal fence can never revive an old instruction. */
    boolean repeated(Instruction requested) {
        if (instruction != null && instruction.sequence() == requested.sequence()) {
            if (!instruction.equals(requested)) throw new IllegalArgumentException("That priority sequence belongs to another instruction");
            return true;
        }
        if (requested.sequence() <= sequence) throw new IllegalArgumentException("That priority instruction has been withdrawn or replaced");
        if (instruction != null && instruction.grantId().equals(requested.grantId()))
            throw new IllegalArgumentException("That priority identity belongs to another sequence");
        return false;
    }

    void begin(Instruction requested) {
        if (repeated(requested)) return;
        sequence = requested.sequence(); instruction = requested; passes = 0;
        status = Status.HELD; reason = "checking-response";
    }

    void revokeThrough(long through) {
        if (through <= 0) throw new IllegalArgumentException("A priority withdrawal needs its instruction sequence");
        sequence = Math.max(sequence, through);
        if (instruction == null) { status = Status.REVOKED; reason = "explicit"; return; }
        if (instruction.sequence() <= through) revoke("explicit");
    }

    void hold(String why) { if (accepted()) { status = Status.HELD; reason = why; } }
    void ready() { if (accepted()) { status = Status.ACTIVE; reason = ""; } }
    void passed() { if (accepted()) { if (passes == Integer.MAX_VALUE) revoke("pass-limit"); else passes++; } }
    void revoke(String why) { if (accepted()) { status = Status.REVOKED; reason = why; } }

    /** SyncTransport has already retired the old connection generation. Old
     * requests cannot enter this reset connection's instruction namespace. */
    void disconnected() { sequence = 0; instruction = null; status = Status.IDLE; reason = "connection-ended"; passes = 0; }

    Frame frame() {
        return new Frame(sequence, instruction == null ? "" : instruction.grantId(), instruction == null ? null : instruction.mode(),
                status, reason, passes, instruction == null ? List.of() : instruction.phaseStops());
    }

    static boolean identifier(String value) {
        return value != null && !value.isBlank() && value.length() <= 256 && value.chars().noneMatch(Character::isISOControl);
    }
}
