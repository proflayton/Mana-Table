package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual controller windows, including withdrawal before queued rules work.
 * Neither a renderer's highlight nor a second action scan authorizes a pass. */
public class PriorityContinuationExperienceTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void completedPriorityResponseEvidenceNeverLabelsARequiredChoiceAsPriority() throws Exception {
        try (var game = scenario("standing-response-evidence-schema")) {
            var required = game.until(input -> text(input.prompt(), "kind").equals("input")
                    && !text(input.prompt(), "inputType").equals("InputPassPriority"));
            assertFalse(required.prompt().has("responseAvailability"), "Required choices cannot carry a priority-only response certificate");
            assertFalse(required.prompt().get("canAutoPass").getAsBoolean());
        }
    }

    @Test(timeOut = 90000)
    public void ownPhaseStopHoldsAcceptedConsentWithoutConsumingOrRecreatingTheInput() throws Exception {
        try (var game = scenario("standing-own-phase")) {
            var main = main(game); var input = actual(game); acknowledge(game, 0);
            var grant = grant(main, 1, "own-phase", "NoResponse", List.of("MAIN1"), "");
            var state = JSON.toJsonTree(game.session(0).action(grant)).getAsJsonObject();
            assertEquals(text(state.getAsJsonObject("priorityContinuation"), "reasonCode"), "phase-stop");
            assertEquals(text(state.getAsJsonObject("priorityContinuation"), "status"), "Held");
            assertTrue(input.isPriorityActive()); assertEquals(text(state.getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"));
            var revoke = request(main, Map.of("action", "revokePriorityContinuation", "throughSequence", 1));
            game.session(0).action(revoke);
            game.session(0).action(grant); // exact replay is an acknowledgement, never fresh consent
            assertEquals(text(game.state(0).getAsJsonObject("priorityContinuation"), "status"), "Revoked");
            assertTrue(input.isPriorityActive());
        }
    }

    @Test(timeOut = 90000)
    public void ignoringTheOnlyAvailableActivationResumesTheAcceptedHeldInstruction() throws Exception {
        try (var game = new RulesScenario("standing-ignore-held-activation", Map.of(0, List.of("Arid Mesa", "Spellbook"),
                1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")), "Darksteel Colossus", "Progenitus")) {
            var main = main(game); var input = actual(game); acknowledge(game, 0);
            assertEquals(text(main.prompt(), "responseAvailability"), "Available");
            game.session(0).action(grant(main, 1, "held-activation", "NoResponse", List.of(), ""));
            assertTrue(input.isPriorityActive());
            assertEquals(text(game.state(0).getAsJsonObject("priorityContinuation"), "reasonCode"), "response-available");
            var ability = main.prompt().getAsJsonArray("priorityAbilities").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "sourceName").equals("Arid Mesa")).findFirst().orElseThrow();
            game.session(0).action(request(main, Map.of("action", "setAbilityAutoPass", "abilityId", text(ability, "id"), "ignored", true)));
            HeadlessPlatform.query(game.table, () -> null);
            assertFalse(input.isPriorityActive(), "The existing consent and newly certified absent-response scan must resume without another click");
            assertTrue(game.state(0).getAsJsonObject("priorityContinuation").get("automaticPasses").getAsInt() > 0);
            assertEquals(viewer(game.state(0)).get("life").getAsInt(), 40, "Ignoring Mesa must not activate it");
        }
    }

    @Test(timeOut = 90000)
    public void explicitWithdrawalWinsBeforeTheRealQueuedPass() throws Exception {
        try (var game = scenario("standing-queued-withdrawal")) {
            var main = main(game); var input = actual(game); acknowledge(game, 0);
            try (var barrier = new InputBarrier(game)) {
                game.session(0).action(grant(main, 1, "withdrawn", "NoResponse", List.of(), ""));
                game.session(0).action(request(main, Map.of("action", "revokePriorityContinuation", "throughSequence", 1)));
                barrier.release(); HeadlessPlatform.query(game.table, () -> null);
                assertTrue(input.isPriorityActive(), "Metadata acceptance cannot make a withdrawn queued action irrevocable");
                assertEquals(text(game.state(0).getAsJsonObject("priorityContinuation"), "status"), "Revoked");
            }
        }
    }

    @Test(timeOut = 90000)
    public void unknownCompletedScanStateHoldsTheSameWindowInsteadOfGuessingOrRescanning() throws Exception {
        try (var game = scenario("standing-queued-unknown")) {
            var main = main(game); var input = actual(game); acknowledge(game, 0);
            assertEquals(text(main.prompt(), "responseAvailability"), "Absent");
            try (var barrier = new InputBarrier(game)) {
                game.session(0).action(grant(main, 1, "unknown", "NoResponse", List.of(), ""));
                game.table.game.getAction().withState(() -> input.setAvailabilityVerified(false));
                barrier.release(); HeadlessPlatform.query(game.table, () -> null);
                assertTrue(input.isPriorityActive());
            }
            assertEquals(text(game.state(0).getAsJsonObject("priorityContinuation"), "reasonCode"), "response-unknown");
            assertEquals(text(game.state(0).getAsJsonObject("prompt"), "responseAvailability"), "Unknown");
        }
    }

    @Test(timeOut = 90000)
    public void acceptedNoResponseActuallyPassesAndConnectionRetirementCannotReviveItsInstruction() throws Exception {
        try (var game = scenario("standing-real-pass")) {
            var main = main(game); var input = actual(game); acknowledge(game, 0);
            game.session(0).action(grant(main, 1, "real", "NoResponse", List.of(), ""));
            HeadlessPlatform.query(game.table, () -> null);
            assertFalse(input.isPriorityActive());
            var frame = game.state(0).getAsJsonObject("priorityContinuation");
            assertTrue(frame.get("automaticPasses").getAsInt() > 0);
            game.session(0).revokeFiniteYieldForConnection();
            frame = game.state(0).getAsJsonObject("priorityContinuation");
            assertEquals(text(frame, "status"), "Idle"); assertEquals(frame.get("sequence").getAsLong(), 0L);
            assertFalse(game.state(0).getAsJsonObject("priorityObservation").get("registered").getAsBoolean());
            assertNull(game.session(0).state().get("error"));
        }
    }

    @Test(timeOut = 90000)
    public void quietConsentKeepsItsOriginalTokenAndRetiresOnAnActualPublicMutation() throws Exception {
        try (var game = scenario("standing-quiet-token")) {
            var main = main(game); var input = actual(game); acknowledge(game, 0);
            String token = text(main.prompt(), "quietYieldToken"); assertFalse(token.isEmpty());
            try (var barrier = new InputBarrier(game)) {
                game.session(0).action(grant(main, 1, "quiet", "Quiet", List.of(), token));
                game.table.game.getAction().withState(() -> {
                    var forest = game.table.game.getRegisteredPlayers().get(0).getCardsIn(forge.game.zone.ZoneType.Battlefield)
                            .stream().filter(card -> card.getName().equals("Forest")).findFirst().orElseThrow();
                    forest.setTapped(true);
                    game.table.game.fireEvent(new forge.game.event.GameEventCardTapped(forest, true));
                });
                barrier.release(); HeadlessPlatform.query(game.table, () -> null); assertTrue(input.isPriorityActive());
            }
            var frame = game.state(0).getAsJsonObject("priorityContinuation");
            assertEquals(text(frame, "status"), "Revoked"); assertEquals(text(frame, "reasonCode"), "public-action");
            assertNotEquals(text(game.state(0).getAsJsonObject("prompt"), "quietYieldToken"), token);
        }
    }

    static void acknowledge(RulesScenario game, int seat) {
        var state = game.state(seat); var observed = state.getAsJsonObject("priorityObservation");
        var request = new JsonObject(); request.addProperty("sessionId", text(state, "id"));
        request.addProperty("action", "acknowledgePriorityObservation");
        request.addProperty("boardRevision", observed.get("boardRevision").getAsLong());
        request.addProperty("publicActivitySequence", observed.get("publicActivitySequence").getAsLong());
        game.session(seat).action(request);
    }

    @Test(timeOut = 90000)
    public void aSeatProjectedInsideAnAbortedCohortCannotCertifyUnobservedDelivery() throws Exception {
        try (var game = scenario("standing-aborted-cohort")) {
            main(game);
            HeadlessPlatform.query(game.table, () -> {
                var revision = MatchTable.class.getDeclaredField("boardRevision"); revision.setAccessible(true);
                long intermediate = revision.getLong(game.table) + 1;
                revision.setLong(game.table, intermediate);
                assertTrue(game.session(0).publishView(intermediate));
                var staged = game.state(0).getAsJsonObject("priorityObservation");
                assertEquals(staged.get("boardRevision").getAsLong(), intermediate);
                var forged = new JsonObject(); forged.addProperty("sessionId", game.table.id);
                forged.addProperty("action", "acknowledgePriorityObservation"); forged.addProperty("boardRevision", intermediate);
                forged.addProperty("publicActivitySequence", staged.get("publicActivitySequence").getAsLong());
                assertThrows(IllegalArgumentException.class, () -> game.session(0).action(forged));
                game.table.publish(); // the actual coherent recovery cohort
                acknowledge(game, 0);
                var accepted = game.state(0).getAsJsonObject("priorityObservation");
                assertTrue(accepted.get("registered").getAsBoolean());
                assertEquals(accepted.get("acknowledgedBoardRevision"), accepted.get("boardRevision"));
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void onlyPriorityNotificationsCanElideAfterEveryHumanAcceptsItsOwnPublishedFrame() throws Exception {
        try (var game = scenario("standing-publication-fences")) {
            var main = main(game);
            assertFalse(game.table.canElidePriorityPublication(), "Legacy/unregistered audiences require ordinary publication");
            long board = main.state().get("boardRevision").getAsLong();
            for (int seat = 0; seat < 4; seat++) acknowledge(game, seat);
            assertEquals(game.state(0).get("boardRevision").getAsLong(), board, "Observation metadata does not republish every battlefield");
            HeadlessPlatform.query(game.table, () -> {
                assertTrue(game.table.canElidePriorityPublication());
                var phase = game.table.game.getPhaseHandler();
                game.table.game.fireEvent(new forge.game.event.GameEventPlayerPriority(phase.getPlayerTurn().getView(),
                        phase.getPhase(), phase.getPlayerTurn().getView()));
                assertTrue(game.table.canElidePriorityPublication(), "A routine priority notification is not a board mutation");
                var forest = phase.getPlayerTurn().getCardsIn(forge.game.zone.ZoneType.Battlefield).stream()
                        .filter(card -> card.getName().equals("Forest")).findFirst().orElseThrow();
                forest.setTapped(true); game.table.game.fireEvent(new forge.game.event.GameEventCardTapped(forest, true));
                assertFalse(game.table.canElidePriorityPublication(), "A real public rules mutation must publish before another automatic hop");
                game.table.publish(); assertTrue(game.table.canElidePriorityPublication()); return null;
            });
        }
    }
    private static JsonObject grant(Input main, long sequence, String id, String mode, List<String> phases, String token) {
        return request(main, Map.of("action", "grantPriorityContinuation", "sequence", sequence, "grantId", id,
                "mode", mode, "phaseStops", phases, "quietYieldToken", token));
    }
    private static RulesScenario scenario(String name) throws Exception {
        return new RulesScenario(name, Map.of(0, List.of("Forest", "Forest", "Spellbook"), 1, List.of("Spellbook"),
                2, List.of("Spellbook"), 3, List.of("Spellbook")), "Manalith", "Progenitus");
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static InputPassPriority actual(RulesScenario game) {
        return HeadlessPlatform.query(game.table, () -> (InputPassPriority)((PlayerControllerHuman)
                game.table.game.getRegisteredPlayers().get(0).getController()).getInputQueue().getInput());
    }
    private static final class InputBarrier implements AutoCloseable {
        private final CountDownLatch release = new CountDownLatch(1);
        InputBarrier(RulesScenario game) throws InterruptedException {
            var entered = new CountDownLatch(1);
            HeadlessPlatform.later(() -> {
                entered.countDown();
                try (var suspended = game.table.game.getAction().suspendState()) {
                    if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input queue barrier expired");
                } catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
            });
            assertTrue(entered.await(5, TimeUnit.SECONDS));
        }
        void release() { release.countDown(); }
        @Override public void close() { release(); }
    }
}
