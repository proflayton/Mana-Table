package forge.api;

import java.util.*;

/** One seat's explicit consent for one finite queue. This class never answers
 * rules inputs; MatchSession still owns every actual priority operation. */
final class FiniteStackYield {
    static final int MAXIMUM_MEMBERS = 256;
    record Frame(String grantId, String status, List<String> executionIds, List<String> remainingExecutionIds,
                 long stackActionVersion, long resolutionChoiceVersion, String reasonCode, int automaticPasses) { }
    private String grantId = "", decisionId = "", status = "Idle", reason = "";
    private List<String> members = List.of(), remaining = List.of();
    private List<FiniteStackWitness.Member> witness = List.of();
    private long actionVersion, choiceVersion;
    private int turn, passes;
    private String phase;
    private Map<String, Object> paused;

    boolean active() { return status.equals("Active"); }
    String id() { return grantId; }
    List<String> pinned() { return members; }

    boolean repeated(String id, String decision, List<String> ids, long action, long choice) {
        if (!grantId.equals(id)) return false;
        if (!decisionId.equals(decision) || !new HashSet<>(members).equals(new HashSet<>(ids))
                || actionVersion != action || choiceVersion != choice)
            throw new IllegalArgumentException("That finite yield identity already belongs to another consent");
        return true;
    }

    void begin(String id, String decision, List<String> ids, long action, long choice, int currentTurn, String currentPhase,
               List<FiniteStackWitness.Member> currentQueue) {
        if (active()) throw new IllegalArgumentException("Stop the current finite yield before starting another");
        if (!validId(id) || !validId(decision) || ids == null || ids.isEmpty() || ids.size() > MAXIMUM_MEMBERS
                || ids.stream().anyMatch(value -> !validId(value)) || new HashSet<>(ids).size() != ids.size()
                || action < 0 || choice < 0 || currentQueue == null || currentQueue.size() != ids.size()
                || !new HashSet<>(currentQueue.stream().map(FiniteStackWitness.Member::executionId).toList()).equals(new HashSet<>(ids)))
            throw new IllegalArgumentException("A finite yield needs bounded exact consent");
        grantId = id; decisionId = decision; members = List.copyOf(ids); remaining = members;
        actionVersion = action; choiceVersion = choice; turn = currentTurn; phase = currentPhase;
        witness = List.copyOf(currentQueue);
        status = "Active"; reason = ""; passes = 0; paused = null;
    }

    void update(long action, long choice, int currentTurn, String currentPhase, boolean ended,
                List<FiniteStackWitness.Member> stack, MatchExecutions.Frame executions, Object ownEpisode, Map<String, Object> optional) {
        if (!active()) return;
        var known = new HashMap<String, MatchExecutions.Occurrence>();
        for (var occurrence : executions.occurrences()) known.put(occurrence.id(), occurrence);
        var pending = new ArrayList<String>();
        for (String member : members) {
            var occurrence = known.get(member);
            if (occurrence == null) { revoke("execution-gap"); return; }
            if (!occurrence.step().equals("complete")) pending.add(member);
        }
        remaining = List.copyOf(pending);
        if (ended) { revoke("match-ended"); return; }
        if (action != actionVersion) { revoke("outside-action"); return; }
        if (choice != choiceVersion) { revoke("required-choice"); return; }
        if (turn != currentTurn || !Objects.equals(phase, currentPhase)) { revoke("phase-changed"); return; }
        if (stack == null || !members.containsAll(stack.stream().map(FiniteStackWitness.Member::executionId).toList())) { revoke("stack-changed"); return; }
        var currentIds = stack.stream().map(FiniteStackWitness.Member::executionId).collect(java.util.stream.Collectors.toSet());
        if (!witness.stream().filter(member -> currentIds.contains(member.executionId())).toList().equals(stack)) {
            revoke("stack-terms-changed"); return;
        }
        if (remaining.isEmpty()) { status = "Completed"; paused = null; return; }
        if (ownEpisode != null) {
            if (optional == null || !members.contains(optional.get("producingTriggerExecutionId"))) { revoke("required-choice"); return; }
            if (paused != null && !paused.equals(optional)) { revoke("payment-changed"); return; }
            paused = Map.copyOf(optional);
        } else if (paused != null) revoke("unobserved-choice-completion");
    }

    boolean expectedNegative(Map<String, Object> offer) {
        if (!active() || paused == null || !paused.equals(offer)) return false;
        paused = null; return true;
    }
    void passed() { if (active()) passes++; }
    void revoke(String why) { if (active()) { status = "Revoked"; reason = why; paused = null; } }
    Frame frame(long currentAction, long currentChoice) {
        return new Frame(grantId, status, members, remaining, status.equals("Idle") ? currentAction : actionVersion,
                status.equals("Idle") ? currentChoice : choiceVersion, reason, passes);
    }
    private static boolean validId(String value) {
        return value != null && !value.isBlank() && value.length() <= 256 && value.chars().noneMatch(Character::isISOControl);
    }
}
