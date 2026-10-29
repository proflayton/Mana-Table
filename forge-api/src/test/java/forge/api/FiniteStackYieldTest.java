package forge.api;

import org.testng.annotations.Test;
import java.util.*;
import static org.testng.Assert.*;

/** Pure consent guard controls. Every actual pass is still tested through MatchSession. */
public class FiniteStackYieldTest {
    @Test public void completionRequiresEveryOriginalReceiptAndKeepsItsFiniteEvidence() {
        var grant = grant();
        update(grant, List.of("a"), frame("a", "resolution", "b", "complete"), null, null);
        assertEquals(grant.frame(3, 5).remainingExecutionIds(), List.of("a"));
        update(grant, List.of(), frame("a", "complete", "b", "complete"), null, null);
        assertEquals(grant.frame(3, 5).status(), "Completed");
        assertEquals(grant.pinned(), List.of("a", "b"));
        assertTrue(grant.repeated("grant", "decision", List.of("b", "a"), 3, 5));
        assertThrows(IllegalArgumentException.class, () -> grant.repeated("grant", "decision", List.of("a"), 3, 5));
        var missing = grant();
        update(missing, List.of(), frame("a", "complete"), null, null);
        assertEquals(missing.frame(3, 5).reasonCode(), "execution-gap", "Disappearance never proves resolution");
    }

    @Test public void outsideActionsChoicesNewStackAndPhaseChangesNeverRefreshConsent() {
        for (String changed : List.of("action", "choice", "phase", "turn", "new", "unknown", "input", "ended")) {
            var grant = grant();
            grant.update(changed.equals("action") ? 4 : 3, changed.equals("choice") ? 6 : 5,
                    changed.equals("turn") ? 2 : 1, changed.equals("phase") ? "END_OF_TURN" : "MAIN1", changed.equals("ended"),
                    changed.equals("unknown") ? null : changed.equals("new") ? queue("a", "new") : queue("a", "b"),
                    frame("a", "stack", "b", "stack"), changed.equals("input") ? new Object() : null, null);
            assertEquals(grant.frame(3, 5).status(), "Revoked", changed);
        }
        var completedAfterResponse = grant();
        completedAfterResponse.update(4, 5, 1, "MAIN1", false, List.of(), frame("a", "complete", "b", "complete"), null, null);
        assertEquals(completedAfterResponse.frame(4, 5).reasonCode(), "outside-action");
    }

    @Test public void onlyExactCertifiedNegativePaymentCanResumeAPausedGrant() {
        var offer = Map.<String, Object>of("producingTriggerExecutionId", "a", "offerId", "first", "paymentKey", "terms");
        var episode = new Object(); var grant = grant();
        update(grant, List.of("b"), frame("a", "cost", "b", "stack"), episode, offer);
        assertTrue(grant.active());
        assertFalse(grant.expectedNegative(Map.of("producingTriggerExecutionId", "a", "offerId", "other", "paymentKey", "terms")));
        assertTrue(grant.expectedNegative(offer));
        update(grant, List.of("b"), frame("a", "complete", "b", "stack"), null, null);
        assertTrue(grant.active());
        var missingNegative = grant();
        update(missingNegative, List.of("b"), frame("a", "cost", "b", "stack"), episode, offer);
        update(missingNegative, List.of("b"), frame("a", "complete", "b", "stack"), null, null);
        assertEquals(missingNegative.frame(3, 5).reasonCode(), "unobserved-choice-completion");
        var changedOffer = grant();
        update(changedOffer, List.of("b"), frame("a", "cost", "b", "stack"), episode, offer);
        update(changedOffer, List.of("b"), frame("a", "cost", "b", "stack"), episode,
                Map.of("producingTriggerExecutionId", "a", "offerId", "first", "paymentKey", "new-terms"));
        assertEquals(changedOffer.frame(3, 5).reasonCode(), "payment-changed");
        var foreign = grant();
        update(foreign, List.of("a", "b"), frame("a", "stack", "b", "stack"), episode,
                Map.of("producingTriggerExecutionId", "uncaptured"));
        assertEquals(foreign.frame(3, 5).reasonCode(), "required-choice");
    }

    @Test public void consentIsBoundedAndAnExplicitStopCannotBeResurrectedByItsOldId() {
        var grant = grant(); grant.passed(); grant.revoke("explicit");
        assertEquals(grant.frame(3, 5).automaticPasses(), 1);
        assertTrue(grant.repeated("grant", "decision", List.of("a", "b"), 3, 5)); assertFalse(grant.active());
        grant.begin("fresh", "decision2", List.of("a", "b"), 3, 5, 1, "MAIN1", queue("a", "b")); assertTrue(grant.active());
        assertThrows(IllegalArgumentException.class, () -> grant.begin("another", "d", List.of("a"), 3, 5, 1, "MAIN1", queue("a")));
        for (var invalid : List.of(List.<String>of(), List.of("a", "a"), List.of(""),
                java.util.stream.IntStream.range(0, 257).mapToObj(String::valueOf).toList())) {
            var empty = new FiniteStackYield();
            assertThrows(IllegalArgumentException.class, () -> empty.begin("id", "decision", invalid, 3, 5, 1, "MAIN1", queue(invalid.toArray(String[]::new))));
        }
    }

    @Test public void alreadyCapturedRetargetsReorderingAndControllerOrSourceChangesStopBeforeAnotherPass() {
        var original = queue("a", "b"); var first = original.get(0);
        for (var altered : List.of(
                new FiniteStackWitness.Member("a", first.controllerId(), first.kind(), first.sourceOccurrence(), null, List.of("player:3")),
                new FiniteStackWitness.Member("a", 2, first.kind(), first.sourceOccurrence(), null, first.targetTerms()),
                new FiniteStackWitness.Member("a", first.controllerId(), "spell", first.sourceOccurrence(), null, first.targetTerms()),
                new FiniteStackWitness.Member("a", first.controllerId(), first.kind(), "new-incarnation", null, first.targetTerms()),
                new FiniteStackWitness.Member("a", first.controllerId(), first.kind(), first.sourceOccurrence(),
                        new ResolutionBatchProjection.Group("new-rule", "source"), first.targetTerms()))) {
            var grant = grant();
            grant.update(3, 5, 1, "MAIN1", false, List.of(altered, original.get(1)), frame("a", "stack", "b", "stack"), null, null);
            assertEquals(grant.frame(3, 5).reasonCode(), "stack-terms-changed");
        }
        var reordered = grant();
        reordered.update(3, 5, 1, "MAIN1", false, queue("b", "a"), frame("a", "stack", "b", "stack"), null, null);
        assertEquals(reordered.frame(3, 5).reasonCode(), "stack-terms-changed");
        var removed = grant();
        removed.update(3, 5, 1, "MAIN1", false, queue("b"), frame("a", "complete", "b", "stack"), null, null);
        assertTrue(removed.active(), "Ordinary resolution may remove members without changing the surviving queue witness");
    }

    private static FiniteStackYield grant() {
        var grant = new FiniteStackYield(); grant.begin("grant", "decision", List.of("a", "b"), 3, 5, 1, "MAIN1", queue("a", "b")); return grant;
    }
    private static void update(FiniteStackYield grant, List<String> stack, MatchExecutions.Frame frame, Object episode, Map<String, Object> offer) {
        grant.update(3, 5, 1, "MAIN1", false, stack == null ? null : queue(stack.toArray(String[]::new)), frame, episode, offer);
    }
    private static List<FiniteStackWitness.Member> queue(String... ids) {
        return Arrays.stream(ids).map(id -> new FiniteStackWitness.Member(id, 1, "trigger", "source", null, List.of("player:2"))).toList();
    }
    private static MatchExecutions.Frame frame(String... states) {
        var values = new ArrayList<MatchExecutions.Occurrence>();
        for (int i = 0; i < states.length; i += 2) values.add(new MatchExecutions.Occurrence(states[i], null, states[i], "trigger", 1,
                "source", "Source", states[i + 1], states[i + 1].equals("complete") ? "resolved" : null, null, null));
        return new MatchExecutions.Frame(1, 1, values, List.of());
    }
}
