package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Explicit quiet passing is a scoped permission, never a broader Auto setting. */
public class QuietYieldTest {
    private static final String REVOKED = "The table has changed. Review the current decision before passing for now.";

    @Test public void baselineKeepsOnlyInputChurnOutAndCopiesFuturePublicMechanicState() {
        var baseline = new QuietYieldBaseline();
        var card = new LinkedHashMap<String, Object>(Map.of("visualId", "public-card", "combatId", "public-occurrence", "faceDown", false,
                "key", "old-prompt-card", "selectable", true, "highlighted", false,
                "states", List.of(Map.of("key", "future-mechanic", "value", 2))));
        var player = new LinkedHashMap<String, Object>(Map.of("id", 0, "priority", true,
                "zones", List.of(Map.of("name", "Battlefield", "cards", List.of(card)))));
        var state = new LinkedHashMap<String, Object>(Map.of("turn", 2, "activePlayerId", 1,
                "players", List.of(player), "stack", List.of(), "states", List.of(), "phaseKey", "MAIN1"));
        String original = baseline.observe(state, 10);
        card.put("key", "another-prompt-card"); card.put("selectable", false); card.put("highlighted", true);
        player.put("priority", false); state.put("phaseKey", "END_OF_TURN");
        state.put("revision", 500L); state.put("prompt", Map.of("id", "another-private-prompt"));
        assertEquals(baseline.observe(state, 10), original, "A phase/priority publication alone cannot silently replace the permission");
        card.put("states", List.of(Map.of("key", "unfamiliar-state-with-the-same-value", "value", 2)));
        assertNotEquals(baseline.observe(state, 10), original, "The generic public mechanic key must remain part of state");
        assertFalse(baseline.permits(original, 10), "Retained source maps cannot mutate the previously copied baseline");
    }

    @Test public void unchangedPublicStatRefreshDoesNotCancelButATransientActualChangeDoes() {
        var baseline = new QuietYieldBaseline();
        var card = new LinkedHashMap<String, Object>(Map.of("visualId", "public-card", "combatId", "public-occurrence",
                "faceDown", true, "power", 2, "toughness", 2));
        var state = Map.<String, Object>of("turn", 2, "activePlayerId", 1, "players", List.of(Map.of("id", 0,
                "life", 40, "states", List.of(), "mana", Map.of("G", 2), "zones", List.of(Map.of("cards", List.of(card))))), "stack", List.of());
        String token = baseline.observe(state, 10);
        baseline.observeCard(card);
        baseline.observePlayer(0, Map.of("life", 40, "states", List.of()));
        baseline.observeMana(0, Map.of("G", 2));
        assertTrue(baseline.permits(token, 10), "Static ability recomputation with identical public values stays quiet");
        card.put("power", 3); baseline.observeCard(card);
        card.put("power", 2); baseline.observeCard(card);
        assertFalse(baseline.permits(token, 10), "A public face-down creature changing and returning to 2/2 is still a change");
        String next = baseline.observe(state, 10);
        baseline.observeMana(0, Map.of("G", 3)); baseline.observeMana(0, Map.of("G", 2));
        assertFalse(baseline.permits(next, 10), "Mana added and spent before the next poll cannot preserve permission");
    }

    @Test public void transientPublicEventsAndRequiredPromptsRevokeEvenWhenTheBoardEndsIdentically() {
        var baseline = new QuietYieldBaseline();
        var state = Map.<String, Object>of("turn", 2, "activePlayerId", 1, "players", List.of(), "stack", List.of());
        String original = baseline.observe(state, 10);
        assertTrue(baseline.permits(original, 10));
        assertFalse(baseline.permits(original, 12), "A spell cast and resolved between polls is still a public action");
        String afterSpell = baseline.observe(state, 12);
        assertNotEquals(afterSpell, original);
        baseline.invalidate();
        assertFalse(baseline.permits(afterSpell, 12), "Resource events and required private choices revoke atomically");
        assertNotEquals(baseline.observe(state, 12), afterSpell);
        assertFalse(baseline.permits("", 12)); assertFalse(baseline.permits("another-seat-token", 12));
    }

    @Test(timeOut = 90000)
    public void explicitPermissionPassesAnAvailableMesaAcrossQuietWindowsWithoutChangingAuto() throws Exception {
        try (var game = scenario("quiet-yield-available-mesa")) {
            var own = game.until(i -> i.seat() == 0 && priority(i) && ownTurn(i));
            assertEquals(token(own), "");
            game.rejected(own, Map.of("action", "passUntilPublicAction", "yieldToken", "unissued"), REVOKED);
            var input = game.until(i -> eligible(i) && text(i.state(), "phaseKey").equals("MAIN2"));
            String token = token(input);
            assertFalse(token.isEmpty());
            assertFalse(input.prompt().get("canAutoPass").getAsBoolean(), "Available Mesa keeps conservative Auto stopped");
            assertTrue(input.state().getAsJsonArray("ignoredPriorityAbilities").isEmpty());
            game.rejected(input, Map.of("action", "passIfNoResponse"), "This response window needs your decision");
            game.rejected(input, Map.of("action", "passUntilPublicAction"), REVOKED);
            game.rejected(input, Map.of("action", "passUntilPublicAction", "yieldToken", "unknown"), REVOKED);
            synchronized (game.table.gate) { game.table.publish(); }
            var refreshed = current(game, 0);
            assertEquals(token(refreshed), token, "Refreshing the same authoritative input retains its permission");
            assertTrue(game.answer(refreshed, Map.of("action", "passUntilPublicAction", "yieldToken", token)));
            var next = game.until(i -> i.seat() == 0 && priority(i));
            assertEquals(next.state().get("turn"), input.state().get("turn"));
            assertEquals(token(next), token, "Quiet end-step priority keeps the same explicit baseline");
            assertFalse(next.prompt().get("canAutoPass").getAsBoolean());
            assertTrue(next.state().getAsJsonArray("ignoredPriorityAbilities").isEmpty());
            assertTrue(game.answer(next, Map.of("action", "passUntilPublicAction", "yieldToken", token)));
            var nextTurn = game.until(i -> i.seat() == 0 && priority(i)
                    && i.state().get("turn").getAsInt() > input.state().get("turn").getAsInt());
            assertNotEquals(token(nextTurn), token, "The same policy must never silently cross a turn");
            game.rejected(nextTurn, Map.of("action", "passUntilPublicAction", "yieldToken", token), REVOKED);
        }
    }

    @Test(timeOut = 90000)
    public void realManaActivationStackAndRequiredSearchReturnControlInsteadOfConsumingAChoice() throws Exception {
        try (var game = scenario("quiet-yield-public-action", true)) {
            var input = game.until(i -> eligible(i) && text(i.state(), "phaseKey").equals("MAIN2"));
            String original = token(input);
            assertTrue(game.answer(input, Map.of("action", "card", "key", text(card(input, "Forest"), "key"))));
            var floating = game.until(i -> eligible(i) && viewer(i.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            assertNotEquals(token(floating), original, "A real mana ability interrupts despite never entering the stack");
            game.rejected(floating, Map.of("action", "passUntilPublicAction", "yieldToken", original), REVOKED);
            String beforeMesa = token(floating);
            assertTrue(game.answer(floating, Map.of("action", "passUntilPublicAction", "yieldToken", beforeMesa)));
            var retained = game.until(i -> i.seat() == 0 && priority(i));
            assertEquals(viewer(retained.state()).getAsJsonObject("mana").get("G").getAsInt(), 1);
            assertEquals(token(retained), beforeMesa, "Upwelling's unchanged retained mana is a refresh, not a new public action");
            assertTrue(game.answer(retained, Map.of("action", "card", "key", text(card(retained, "Arid Mesa"), "key"))));
            var response = game.until(i -> i.seat() == 0 && priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(token(response), "", "A stack response never offers quiet passing");
            game.rejected(response, Map.of("action", "passUntilPublicAction", "yieldToken", beforeMesa), REVOKED);
            var search = game.until(i -> i.seat() == 0 && !priority(i));
            assertEquals(token(search), "");
            game.rejected(search, Map.of("action", "passUntilPublicAction", "yieldToken", beforeMesa), REVOKED);
            assertFalse(text(search.prompt(), "inputType").equals("InputPassPriority"), "The library choice remains required and unanswered");
        }
    }

    @Test(timeOut = 90000)
    public void requiredChoiceRevocationAfterEnqueueRestoresTheExactUnansweredPriority() throws Exception {
        try (var game = scenario("quiet-yield-owned-recheck")) {
            var input = game.until(i -> eligible(i) && text(i.state(), "phaseKey").equals("MAIN1"));
            String token = token(input), promptId = text(input.prompt(), "id");
            var entered = new CountDownLatch(1);
            var release = new CountDownLatch(1);
            HeadlessPlatform.later(() -> {
                entered.countDown();
                try { if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input queue barrier expired"); }
                catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new AssertionError(error); }
            });
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            try {
                // The real input is queued, then receives the same generic public
                // required-decision notification used for another seat's private
                // choice. No card data or fabricated rules action is supplied.
                game.table.seat(0).action(request(input, Map.of("action", "passUntilPublicAction", "yieldToken", token)));
                synchronized (game.table.gate) {
                    game.table.seat(0).updatePublicDecision(null, "selection");
                    game.table.refreshDecisionState();
                }
            } finally { release.countDown(); }
            long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
            JsonObject restored = null;
            while (System.nanoTime() < deadline) {
                var state = game.state(0);
                if (state.has("prompt") && !state.get("prompt").isJsonNull()) { restored = state; break; }
                Thread.sleep(5);
            }
            assertNotNull(restored, "A revoked queued action must restore input rather than leave the table resolving");
            assertEquals(text(restored.getAsJsonObject("prompt"), "id"), promptId);
            assertNotEquals(text(restored.getAsJsonObject("prompt"), "quietYieldToken"), token);
            assertEquals(restored.get("turn"), input.state().get("turn"));
            assertEquals(text(restored, "phaseKey"), text(input.state(), "phaseKey"));
            assertTrue(restored.getAsJsonArray("notices").toString().contains("Passing for now stopped"));
            assertTrue(game.answer(current(game, 0), Map.of("action", "ok")), "Manual priority remains usable after the failed owned recheck");
        }
    }

    private static RulesScenario scenario(String name) throws Exception {
        return scenario(name, false);
    }
    private static RulesScenario scenario(String name, boolean retainedMana) throws Exception {
        return new RulesScenario(name, Map.of(0, retainedMana ? List.of("Arid Mesa", "Forest", "Spellbook", "Upwelling") : List.of("Arid Mesa", "Forest", "Spellbook"),
                1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")), "Mountain", "Progenitus");
    }
    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static boolean ownTurn(RulesScenario.Input input) { return input.state().get("activePlayerId").equals(input.state().get("viewerId")); }
    private static boolean eligible(RulesScenario.Input input) { return input.seat() == 0 && priority(input) && !ownTurn(input) && !token(input).isEmpty(); }
    private static String token(RulesScenario.Input input) { return text(input.prompt(), "quietYieldToken"); }
    private static JsonObject card(RulesScenario.Input input, String name) {
        return zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static RulesScenario.Input current(RulesScenario game, int seat) {
        var state = game.state(seat); return new RulesScenario.Input(seat, state, state.getAsJsonObject("prompt"));
    }
    private static JsonObject request(RulesScenario.Input input, Map<String, ?> answer) {
        var result = new com.google.gson.Gson().toJsonTree(answer).getAsJsonObject();
        result.addProperty("sessionId", text(input.state(), "id")); result.addProperty("promptId", text(input.prompt(), "id"));
        return result;
    }
}
