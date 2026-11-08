package forge.api;

import java.util.List;
import java.util.Map;
import java.util.Objects;

/** A closed control receipt from the exact committed frame returned by the
 * action. It carries delivery and consent facts, never a new rules domain. */
record AutomatedPriorityMetadata(int schemaVersion, String gameId, int seatId,
        long revision, long boardRevision, long stackInterruptionVersion, int turn,
        String status, boolean playerActive, Integer decisionOwnerId, Integer priorityPlayerId,
        String decisionId, String priorityDecisionId, PriorityContinuation.Frame continuation,
        PriorityObservation.Frame observation) {
    static AutomatedPriorityMetadata from(Map<String, Object> frame) {
        int seat = ((Number)frame.get("viewerId")).intValue();
        int turn = ((Number)frame.get("turn")).intValue();
        String status = (String)frame.get("status");
        boolean active = false;
        int matchingViewers = 0;
        if (frame.get("players") instanceof List<?> players) for (Object value : players) {
            if (value instanceof Map<?, ?> player && Objects.equals(player.get("id"), seat)) {
                matchingViewers++;
                active = Boolean.FALSE.equals(player.get("eliminated"));
            }
        }
        active = matchingViewers == 1 && active;
        Integer decisionOwner = (Integer)frame.get("decisionOwnerId");
        Integer priorityOwner = (Integer)frame.get("priorityPlayerId");
        Map<?, ?> prompt = frame.get("prompt") instanceof Map<?, ?> value ? value : null;
        String decision = prompt != null && prompt.get("id") instanceof String value ? value : null;
        String priority = active && turn > 0 && "playing".equals(status)
                && Objects.equals(decisionOwner, seat) && Objects.equals(priorityOwner, seat)
                && prompt != null && "input".equals(prompt.get("kind")) && "priority".equals(prompt.get("task"))
                && Boolean.TRUE.equals(prompt.get("okEnabled")) && decision != null && !decision.isEmpty() ? decision : null;
        return new AutomatedPriorityMetadata(1, (String)frame.get("id"), seat,
                ((Number)frame.get("revision")).longValue(), ((Number)frame.get("boardRevision")).longValue(),
                ((Number)frame.get("stackInterruptionVersion")).longValue(), turn, status, active,
                decisionOwner, priorityOwner, decision, priority,
                (PriorityContinuation.Frame)frame.get("priorityContinuation"), (PriorityObservation.Frame)frame.get("priorityObservation"));
    }
}
