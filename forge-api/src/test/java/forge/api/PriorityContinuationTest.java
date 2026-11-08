package forge.api;

import org.testng.annotations.Test;
import java.util.List;
import static org.testng.Assert.*;

public class PriorityContinuationTest {
    @Test public void withdrawalBeforeInstallationFencesTheDelayedInstruction() {
        var owner = new PriorityContinuation(); owner.revokeThrough(8);
        assertEquals(owner.frame().sequence(), 8L);
        assertEquals(owner.frame().status(), PriorityContinuation.Status.REVOKED);
        assertThrows(IllegalArgumentException.class, () -> owner.begin(instruction(8, "delayed")));
        owner.begin(instruction(9, "fresh")); assertTrue(owner.accepted());
    }
    @Test public void staleWithdrawalCannotRetireItsReplacementAndReplayCannotResurrectConsent() {
        var owner = new PriorityContinuation(); var first = instruction(1, "first"); owner.begin(first); owner.ready(); owner.passed();
        var second = instruction(2, "second"); owner.begin(second); owner.ready(); owner.revokeThrough(1);
        assertTrue(owner.current(second)); assertEquals(owner.frame().sequence(), 2L);
        owner.revokeThrough(2); assertTrue(owner.repeated(second)); owner.begin(second); assertFalse(owner.accepted());
        assertEquals(owner.frame().automaticPasses(), 0);
        assertThrows(IllegalArgumentException.class, () -> owner.repeated(instruction(2, "different")));
        owner.disconnected(); assertEquals(owner.frame().sequence(), 0L); assertNull(owner.frame().mode());
    }
    @Test public void heldInstructionsKeepTheirPolicyAndOriginalQuietToken() {
        var owner = new PriorityContinuation();
        var original = new PriorityContinuation.Instruction(1, "quiet", PriorityContinuation.Mode.QUIET, List.of("MAIN1"), "original");
        owner.begin(original); owner.hold("required-choice"); assertTrue(owner.accepted());
        assertSame(owner.instruction(), original); owner.ready(); owner.passed();
        assertEquals(owner.frame().automaticPasses(), 1); owner.revoke("public-action");
        assertEquals(owner.instruction().quietYieldToken(), "original"); assertFalse(owner.accepted());
        assertThrows(IllegalArgumentException.class, () -> owner.repeated(new PriorityContinuation.Instruction(1, "quiet",
                PriorityContinuation.Mode.QUIET, List.of("MAIN1"), "recaptured")));
    }
    @Test public void phasePoliciesAreClosedAndNeverAcceptAmbiguousInstructions() {
        assertThrows(IllegalArgumentException.class, () -> new PriorityContinuation.Instruction(1, "a", PriorityContinuation.Mode.NO_RESPONSE, List.of("MAIN1", "MAIN1"), ""));
        assertThrows(IllegalArgumentException.class, () -> new PriorityContinuation.Instruction(1, "a", PriorityContinuation.Mode.NO_RESPONSE, List.of("opponentTurn"), ""));
        assertThrows(IllegalArgumentException.class, () -> new PriorityContinuation.Instruction(1, "a", PriorityContinuation.Mode.QUIET, List.of(), ""));
        assertThrows(IllegalArgumentException.class, () -> new PriorityContinuation.Instruction(1, "a", PriorityContinuation.Mode.NO_RESPONSE, List.of(), "token"));
    }
    @Test public void onlyActuallyPublishedAudienceCursorsReplenishBoundedCredit() {
        var observation = new PriorityObservation(); observation.published(3, 0);
        assertFalse(observation.hasCredit(0));
        assertThrows(IllegalArgumentException.class, () -> observation.acknowledge(4, 0));
        assertThrows(IllegalArgumentException.class, () -> observation.acknowledge(3, 1));
        observation.acknowledge(3, 0); assertTrue(observation.hasCredit(63)); assertFalse(observation.hasCredit(64));
        observation.published(4, 64); observation.acknowledge(4, 64); assertTrue(observation.hasCredit(64));
        assertEquals(observation.frame(false).acknowledgedPublicActivitySequence(), 64L);
        observation.disconnected(); assertFalse(observation.registered()); assertFalse(observation.initialized());
        assertThrows(IllegalArgumentException.class, () -> observation.acknowledge(4, 64));
    }
    private static PriorityContinuation.Instruction instruction(long sequence, String id) {
        return new PriorityContinuation.Instruction(sequence, id, PriorityContinuation.Mode.NO_RESPONSE, List.of(), "");
    }
}
