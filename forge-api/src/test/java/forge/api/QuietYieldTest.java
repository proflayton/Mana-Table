package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.time.Duration;
import java.util.ArrayList;
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

    @Test public void combatPermissionTracksDeclaredRelationshipsInsteadOfInputOptions() {
        var baseline = new QuietYieldBaseline();
        var state = new LinkedHashMap<String, Object>(Map.of("turn", 2, "activePlayerId", 1,
                "players", List.of(), "stack", List.of()));
        String quiet = baseline.observe(state, 10);
        var combat = new LinkedHashMap<String, Object>(Map.of("attackingPlayerId", 1, "attackers", List.of(),
                "defenders", List.of(Map.of("kind", "player", "id", 0)),
                "attackerCandidates", List.of("attacker"), "selectedDefender", Map.of("kind", "player", "id", 0),
                "attackOptions", List.of(Map.of("cardId", "attacker"))));
        state.put("combat", combat);
        assertEquals(baseline.observe(state, 10), quiet, "An empty combat shell and attack choices are not an attack");
        var attacker = new LinkedHashMap<String, Object>(Map.of("cardId", "attacker", "defender", Map.of("kind", "player", "id", 0),
                "blockerIds", List.of(), "blocked", false, "eligibleBlockerIds", List.of("first-option")));
        combat.put("attackers", List.of(attacker));
        String declared = baseline.observe(state, 10);
        assertNotEquals(declared, quiet, "An actual attacker interrupts even without a new activity entry");
        attacker.put("eligibleBlockerIds", List.of("different-option"));
        combat.put("blockerCandidates", List.of("different-option"));
        assertEquals(baseline.observe(state, 10), declared, "Legal block options alone cannot replace declared combat");
        attacker.put("blockerIds", List.of("actual-blocker"));
        attacker.put("blocked", true);
        String blocked = baseline.observe(state, 10);
        assertNotEquals(blocked, declared, "Assigning an actual blocker interrupts");
        attacker.put("defender", Map.of("kind", "player", "id", 2));
        assertNotEquals(baseline.observe(state, 10), blocked, "Changing the actual defender interrupts");
    }

    @Test public void turnDrawAdvancesOnlyTheExactExpectedOpponentZoneCounts() {
        var baseline = new QuietYieldBaseline();
        var hand = new LinkedHashMap<String, Object>(Map.of("name", "Hand", "count", 7, "cards", List.of()));
        var library = new LinkedHashMap<String, Object>(Map.of("name", "Library", "count", 20, "cards", List.of()));
        var state = Map.<String, Object>of("turn", 2, "activePlayerId", 1, "players", List.of(Map.of("id", 1,
                "zones", List.of(hand, library))), "stack", List.of());
        String original = baseline.observe(state, 10);
        baseline.observeTurnBasedDraw(1);
        hand.put("count", 8); library.put("count", 19);
        assertEquals(baseline.observe(state, 10), original, "Only the tagged opponent turn draw advances expected counts");
        hand.put("count", 9); library.put("count", 18);
        assertNotEquals(baseline.observe(state, 10), original, "An uncredited effect draw remains a meaningful zone change");

        original = baseline.observe(state, 10);
        baseline.observeTurnBasedDraw(0);
        hand.put("count", 10); library.put("count", 17);
        assertNotEquals(baseline.observe(state, 10), original, "A draw for another player cannot credit these zone counts");
        original = baseline.observe(state, 10);
        baseline.observeTurnBasedDraw(1);
        hand.put("count", 11); library.put("count", 16);
        library.put("topCard", Map.of("name", "A newly revealed card"));
        assertNotEquals(baseline.observe(state, 10), original, "A turn draw cannot hide new public library information");
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

    @DataProvider public Object[][] emptyCombats() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "emptyCombats", timeOut = 90000)
    public void availableInstantKeepsExplicitPermissionAcrossEmptyCombat(boolean chooseNoAttackers) throws Exception {
        try (var game = new RulesScenario("quiet-yield-instant-empty-combat-" + chooseNoAttackers,
                Map.of(0, List.of("Mountain", "Spellbook"),
                        1, chooseNoAttackers ? List.of("Raging Goblin", "Spellbook") : List.of("Spellbook"),
                        2, List.of("Spellbook"), 3, List.of("Spellbook")), "Lightning Bolt", "Progenitus")) {
            int opponent = game.table.game.getPlayers().get(1).getId();
            var input = game.until(i -> eligible(i) && i.state().get("activePlayerId").getAsInt() == opponent
                    && text(i.state(), "phaseKey").equals("UPKEEP"));
            String original = token(input);
            int turn = input.state().get("turn").getAsInt();
            var checkpoints = new ArrayList<JsonObject>();
            boolean choseNone = false;
            boolean sawDraw = false, sawMain = false;
            try {
                assertTrue(zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .anyMatch(card -> text(card.getAsJsonObject(), "name").equals("Lightning Bolt")),
                        "The viewer has a real instant in hand and an untapped Mountain to cast it");
                for (int windows = 0; windows < 10; windows++) {
                    checkpoints.add(input.state().deepCopy());
                    sawDraw |= text(input.state(), "phaseKey").equals("DRAW");
                    sawMain |= text(input.state(), "phaseKey").equals("MAIN1");
                    assertEquals(input.state().get("turn").getAsInt(), turn);
                    assertTrue(input.state().getAsJsonArray("stack").isEmpty());
                    var combat = input.state().get("combat");
                    assertTrue(combat == null || combat.isJsonNull() || combat.getAsJsonObject().getAsJsonArray("attackers").isEmpty(),
                            "Nobody has declared an attacker; this is an empty combat, not an ignored attack");
                    assertFalse(input.prompt().get("canAutoPass").getAsBoolean(),
                            "The affordable instant must keep ordinary Auto conservative in every window");
                    assertEquals(token(input), original,
                            "Explicit permission must survive quiet UPKEEP -> DRAW -> MAIN1 -> COMBAT_BEGIN -> MAIN2: "
                                    + text(input.state(), "phaseKey") + "; attacker selection completed=" + choseNone);
                    if (text(input.state(), "phaseKey").equals("MAIN2")) {
                        assertEquals(choseNone, chooseNoAttackers, "A real opponent attack selection must explicitly choose none");
                        assertTrue(sawDraw && sawMain, "The same permission must cross the actual turn draw and first main phase");
                        var turnDraw = input.state().getAsJsonArray("activity").asList().stream()
                                .map(entry -> entry.getAsJsonObject()).filter(entry -> entry.get("turn").getAsInt() == turn
                                        && text(entry, "phaseKey").equals("DRAW") && entry.has("playerId") && !entry.get("playerId").isJsonNull()
                                        && entry.get("playerId").getAsInt() == opponent && List.of("moved", "draw").contains(text(entry, "kind"))).toList();
                        assertEquals(turnDraw.size(), 2, "Both the routine library-to-hand move and draw remain in history");
                        assertTrue(turnDraw.stream().allMatch(entry -> !entry.get("interruptsQuietYield").getAsBoolean()),
                                "Exact scheduled draw history is retained without revoking the explicit yield");
                        return;
                    }
                    assertTrue(game.answer(input, Map.of("action", "passUntilPublicAction", "yieldToken", original)));
                    input = game.until(i -> i.seat() == 0 && priority(i)
                            || i.seat() == 1 && text(i.prompt(), "inputType").equals("InputAttack"));
                    if (text(input.prompt(), "inputType").equals("InputAttack")) {
                        checkpoints.add(input.state().deepCopy());
                        assertTrue(chooseNoAttackers);
                        assertTrue(game.answer(input, Map.of("action", "ok")), "The opponent deliberately declares no attackers");
                        choseNone = true;
                        input = game.until(i -> i.seat() == 0 && priority(i));
                    }
                }
                fail("The quiet draw and combat did not reach the second main phase within ten local priority windows");
            } finally {
                Files.writeString(game.evidence.resolve("empty-combat-boundaries.json"),
                        new com.google.gson.GsonBuilder().setPrettyPrinting().serializeNulls().create().toJson(checkpoints));
            }
        }
    }

    @DataProvider public Object[][] drawInterruptions() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "drawInterruptions", timeOut = 90000)
    public void routineTurnDrawDoesNotHideATriggerOrLandPlay(boolean drawTrigger) throws Exception {
        try (var game = new RulesScenario("quiet-yield-draw-interruption-" + drawTrigger,
                Map.of(0, List.of("Arid Mesa", "Mountain", "Spellbook"),
                        1, drawTrigger ? List.of("Howling Mine", "Spellbook") : List.of("Spellbook"),
                        2, List.of("Spellbook"), 3, List.of("Spellbook")),
                drawTrigger ? "Lightning Bolt" : "Mountain", "Progenitus")) {
            int opponent = game.table.game.getPlayers().get(1).getId();
            var input = game.until(i -> eligible(i) && i.state().get("activePlayerId").getAsInt() == opponent
                    && text(i.state(), "phaseKey").equals("UPKEEP"));
            String original = token(input);
            assertFalse(input.prompt().get("canAutoPass").getAsBoolean());
            assertTrue(game.answer(input, Map.of("action", "passUntilPublicAction", "yieldToken", original)));
            if (drawTrigger) {
                input = game.until(i -> i.seat() == 0 && priority(i));
                assertFalse(input.state().getAsJsonArray("stack").isEmpty(), "Howling Mine's draw-step trigger must have a response window");
                assertEquals(token(input), "", "The routine draw must not authorize passing the new trigger");
                assertTrue(input.state().getAsJsonArray("activity").asList().stream().map(entry -> entry.getAsJsonObject())
                        .anyMatch(entry -> text(entry, "kind").equals("ability") && text(entry, "cardName").equals("Howling Mine")
                                && entry.get("interruptsQuietYield").getAsBoolean()), "The real draw-step trigger is an interrupting public event");
            } else {
                for (int windows = 0; windows < 4; windows++) {
                    input = game.until(i -> i.seat() == 0 && priority(i)
                            || i.seat() == 1 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
                    if (input.seat() == 1) break;
                    assertEquals(token(input), original, "The scheduled turn draw preserves the original permission");
                    assertTrue(game.answer(input, Map.of("action", "passUntilPublicAction", "yieldToken", original)));
                }
                assertEquals(input.seat(), 1, "The active opponent reaches their main phase before the land play");
                var land = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Mountain")).findFirst().orElseThrow();
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(land, "key"))));
                input = game.until(i -> i.seat() == 0 && priority(i));
                assertTrue(input.state().getAsJsonArray("stack").isEmpty(), "Playing a land interrupts without using the stack");
                assertNotEquals(token(input), original, "A real land play remains a new public action after the routine draw");
                assertTrue(input.state().getAsJsonArray("activity").asList().stream().map(entry -> entry.getAsJsonObject())
                        .anyMatch(entry -> text(entry, "kind").equals("land") && entry.get("interruptsQuietYield").getAsBoolean()),
                        "The land play remains an interrupting public history entry");
            }
            game.rejected(input, Map.of("action", "passUntilPublicAction", "yieldToken", original), REVOKED);
            if (drawTrigger) {
                input = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
                assertTrue(input.state().getAsJsonArray("activity").asList().stream().map(entry -> entry.getAsJsonObject())
                        .anyMatch(entry -> text(entry, "kind").equals("draw") && text(entry, "phaseKey").equals("DRAW")
                                && !entry.get("playerId").isJsonNull() && entry.get("playerId").getAsInt() == opponent
                                && entry.get("interruptsQuietYield").getAsBoolean()), "The additional effect draw is interrupting, unlike the scheduled draw");
            }
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
