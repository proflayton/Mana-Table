package forge.api;

import java.util.*;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Rule outcomes at actual human decision boundaries, not renderer fixtures. */
public class RulesRegressionTest {
    @Test(timeOut = 90000)
    public void removingReliquaryTowerRestoresTheLimitBeforeCleanup() throws Exception {
        try (var game = new RulesScenario("remove-no-limit", Map.of(0, List.of("Reliquary Tower", "Wasteland")))) {
            var main = game.until(i -> i.state().get("turn").getAsInt() == 1
                    && text(i.state(), "phaseKey").equals("MAIN1") && i.seat() == 0);
            assertTrue(viewer(main.state()).get("maxHandSize").isJsonNull());
            var wasteland = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Wasteland")).findFirst().orElseThrow();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(wasteland, "key"))));
            var choice = game.until(i -> i.seat() == 0);
            assertEquals(text(choice.prompt(), "kind"), "choice");
            var options = choice.prompt().getAsJsonArray("choices");
            int ability = -1;
            for (int i = 0; i < options.size(); i++) if (text(options.get(i).getAsJsonObject(), "label").contains("Destroy")) ability = i;
            assertTrue(ability >= 0, "Wasteland must offer its destruction ability: " + options);
            assertTrue(game.answer(choice, Map.of("choices", List.of(ability))));
            var target = game.until(i -> i.seat() == 0);
            assertEquals(text(target.prompt(), "inputType"), "InputSelectTargets");
            var tower = zone(viewer(target.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Reliquary Tower")).findFirst().orElseThrow();
            assertTrue(tower.get("selectable").getAsBoolean());
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(tower, "key"))));
            var cleanup = game.until(i -> text(i.state(), "phaseKey").equals("CLEANUP"));
            assertEquals(cleanup.seat(), 0);
            assertEquals(viewer(cleanup.state()).get("maxHandSize").getAsInt(), 7);
            assertEquals(count(viewer(cleanup.state()), "Hand"), 8);
            assertEquals(count(viewer(cleanup.state()), "Battlefield"), 0);
            assertEquals(count(viewer(cleanup.state()), "Graveyard"), 2);
            var discard = zone(viewer(cleanup.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(cleanup, Map.of("action", "card", "key", text(discard, "key"))));
            game.until(i -> i.state().get("turn").getAsInt() == 2);
            assertEquals(count(viewer(game.state(0)), "Hand"), 7);
            assertEquals(count(viewer(game.state(0)), "Graveyard"), 3);
        }
    }

    @Test(timeOut = 90000)
    public void cleanupDiscardTriggersResolveBeforeTheNextTurn() throws Exception {
        try (var game = new RulesScenario("cleanup-trigger", Map.of(0, List.of("Bag of Holding")))) {
            var cleanup = game.until(i -> text(i.state(), "phaseKey").equals("CLEANUP"));
            assertEquals(cleanup.seat(), 0);
            int ownerId = cleanup.state().get("viewerId").getAsInt();
            var discard = zone(viewer(cleanup.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(cleanup, Map.of("action", "card", "key", text(discard, "key"))));
            Set<Integer> responders = new HashSet<>();
            game.until(i -> {
                if (text(i.state(), "phaseKey").equals("CLEANUP") && i.state().getAsJsonArray("stack").size() == 1) {
                    assertEquals(text(i.prompt(), "inputType"), "InputPassPriority");
                    assertEquals(count(player(i.state(), ownerId), "Graveyard"), 1);
                    responders.add(i.seat());
                }
                return i.state().get("turn").getAsInt() == 2;
            });
            assertEquals(responders.size(), 4, "Every player can respond to a cleanup discard trigger");
            var player = viewer(game.state(0));
            assertEquals(count(player, "Hand"), 7);
            assertEquals(count(player, "Graveyard"), 0);
            assertEquals(count(player, "Exile"), 1, "Bag of Holding resolved during cleanup");
        }
    }

    @Test(timeOut = 90000)
    public void commanderDiscardsOnlyDuringOwnCleanupAndAutoCannotSkipIt() throws Exception {
        try (var game = new RulesScenario("commander-cleanup", Map.of())) {
            Set<Integer> cleaned = new HashSet<>();
            Set<Integer> endSteps = new HashSet<>();
            while (cleaned.size() < 4) {
                var input = game.until(i -> {
                    var state = i.state();
                    if (state.get("turn").getAsInt() < 1) return false;
                    int active = state.get("activePlayerId").getAsInt();
                    if (text(state, "phaseKey").equals("END_OF_TURN")) {
                        assertEquals(count(player(state, active), "Hand"), 8, "Drawn cards stay in hand throughout the end step");
                        endSteps.add(active);
                    }
                    return text(state, "phaseKey").equals("CLEANUP");
                });
                var state = input.state();
                int active = state.get("activePlayerId").getAsInt();
                assertTrue(endSteps.contains(active));
                assertEquals(state.get("viewerId").getAsInt(), active, "Only the active player discards");
                assertEquals(text(input.prompt(), "inputType"), "InputSelectCardsFromList");
                assertFalse(input.prompt().get("canAutoPass").getAsBoolean());
                game.rejected(input, "passIfNoResponse", "This response window needs your decision");
                game.rejected(input, "ok", "Continue is not available");
                game.rejected(input, "cancel", "Cancel is not available");
                var hand = zone(viewer(state), "Hand").getAsJsonArray("cards");
                assertEquals(hand.size(), 8);
                var discard = hand.get(0).getAsJsonObject();
                assertTrue(discard.get("selectable").getAsBoolean());
                assertEquals(viewer(state).get("maxHandSize").getAsInt(), 7);
                if (!game.answer(input, Map.of("action", "card", "key", text(discard, "key")))) continue;
                game.until(i -> i.state().get("turn").getAsInt() > state.get("turn").getAsInt());
                var next = game.state(input.seat());
                assertEquals(count(player(next, active), "Hand"), 7);
                assertEquals(count(player(next, active), "Graveyard"), 1);
                cleaned.add(active);
            }
            assertEquals(endSteps.size(), 4);
        }
    }

    @DataProvider
    public Object[][] limits() {
        return new Object[][] {
            {"extra-draw", Map.of(0, List.of("Howling Mine")), new Integer[] {7, 7, 7, 7}, new int[] {9, 9, 9, 9}},
            {"increased-limit", Map.of(0, List.of("Minamo Scrollkeeper")), new Integer[] {8, 7, 7, 7}, new int[] {8, 8, 8, 8}},
            {"reduced-limit", Map.of(0, List.of("Gnat Miser")), new Integer[] {7, 6, 6, 6}, new int[] {8, 8, 8, 8}},
            {"zero-limit", Map.of(0, Collections.nCopies(7, "Gnat Miser")), new Integer[] {7, 0, 0, 0}, new int[] {8, 8, 8, 8}},
            {"set-limit", Map.of(0, List.of("Null Profusion")), new Integer[] {2, 7, 7, 7}, new int[] {7, 8, 8, 8}},
            {"no-limit-land", Map.of(0, List.of("Reliquary Tower")), new Integer[] {null, 7, 7, 7}, new int[] {8, 8, 8, 8}},
            {"no-limit-artifact", Map.of(0, List.of("Thought Vessel")), new Integer[] {null, 7, 7, 7}, new int[] {8, 8, 8, 8}},
            {"no-limit-with-reduction", Map.of(0, List.of("Spellbook"), 1, List.of("Gnat Miser")), new Integer[] {null, 7, 6, 6}, new int[] {8, 8, 8, 8}}
        };
    }

    @Test(dataProvider = "limits", timeOut = 90000)
    public void cardEffectsDetermineEachPlayersCleanupLimit(String name, Map<Integer, List<String>> battlefield,
                                                            Integer[] limits, int[] before) throws Exception {
        try (var game = new RulesScenario(name, battlefield)) {
            Map<Integer, Integer> seatIds = new HashMap<>();
            int[] selected = new int[4];
            Set<Integer> endSteps = new HashSet<>();
            while (true) {
                var input = game.until(i -> {
                    var state = i.state();
                    int turn = state.get("turn").getAsInt();
                    if (turn < 1) return false;
                    seatIds.put(i.seat(), state.get("viewerId").getAsInt());
                    var limit = viewer(state).get("maxHandSize");
                    assertNotNull(limit, "Hand-size limit must be exposed to every client");
                    if (limits[i.seat()] == null) assertTrue(limit.isJsonNull());
                    else assertEquals(limit.getAsInt(), limits[i.seat()].intValue());
                    if (turn > 4) return true;
                    if (text(state, "phaseKey").equals("END_OF_TURN") && state.get("activePlayerId").equals(state.get("viewerId"))) {
                        assertEquals(count(viewer(state), "Hand"), before[i.seat()], "No premature cleanup discard");
                        endSteps.add(i.seat());
                    }
                    return text(i.prompt(), "inputType").equals("InputSelectCardsFromList");
                });
                if (input.state().get("turn").getAsInt() > 4) break;
                assertEquals(text(input.state(), "phaseKey"), "CLEANUP");
                assertEquals(input.state().get("viewerId"), input.state().get("activePlayerId"));
                assertNotNull(limits[input.seat()], "An unlimited hand must never be forced to discard at cleanup");
                assertFalse(input.prompt().get("canAutoPass").getAsBoolean());
                assertFalse(input.prompt().get("okEnabled").getAsBoolean(), "Cannot confirm an incomplete selection");
                var hand = zone(viewer(input.state()), "Hand").getAsJsonArray("cards");
                assertEquals(hand.size(), before[input.seat()], "Cards stay in hand until the entire required selection is made");
                var card = hand.asList().stream().map(c -> c.getAsJsonObject())
                        .filter(c -> c.get("selectable").getAsBoolean() && !c.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
                if (game.answer(input, Map.of("action", "card", "key", text(card, "key")))) selected[input.seat()]++;
            }
            assertEquals(endSteps.size(), 4, "Every seat reached its end step");
            var finalState = game.state(0);
            for (int seat = 0; seat < 4; seat++) {
                int expected = limits[seat] == null ? 0 : Math.max(0, before[seat] - limits[seat]);
                assertEquals(selected[seat], expected, "Exact required discard count for seat " + seat);
                assertEquals(count(player(finalState, seatIds.get(seat)), "Graveyard"), expected);
                // Turn five's active player may already have drawn; compare hand + library
                // so that this assertion doesn't depend on publication of the upkeep.
                var player = player(finalState, seatIds.get(seat));
                assertEquals(count(player, "Hand") + count(player, "Library"), 99 - expected);
            }
        }
    }
}
