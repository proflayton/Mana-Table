package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** An ignored activation changes priority stops, never the legal actions of the game. */
public class AbilityAutoPassTest {
    @Test(timeOut = 90000)
    public void mesaStopsOnlyUntilIgnoredAndReturnsAtTheStartOfItsControllersNextTurn() throws Exception {
        try (var game = scenario("ability-ignore-next-turn", List.of("Arid Mesa"), "Darksteel Colossus")) {
            var main = main(game);
            var mesa = ability(main, "Arid Mesa");
            assertFalse(main.prompt().get("canAutoPass").getAsBoolean(), "An available fetch activation initially stops Auto");
            assertTrue(ignored(main.state()).isEmpty());
            assertFalse(mesa.get("ignoredUntilNextTurn").getAsBoolean());
            long revision = main.state().get("revision").getAsLong();
            var changed = setIgnored(game, main, mesa, true);
            assertTrue(changed.state().get("revision").getAsLong() > revision);
            assertTrue(changed.prompt().get("canAutoPass").getAsBoolean(), "Ignoring the sole activation permits Auto");
            assertTrue(ability(changed, "Arid Mesa").get("ignoredUntilNextTurn").getAsBoolean());
            assertEquals(ignored(changed.state()).size(), 1);
            assertFalse(mesa.get("ignoredUntilNextTurn").getAsBoolean(), "Previously published snapshots remain immutable");
            assertEquals(count(viewer(changed.state()), "Battlefield"), 2);
            assertEquals(viewer(changed.state()).get("life").getAsInt(), 40);
            assertTrue(changed.state().getAsJsonArray("stack").isEmpty(), "Changing a stop does not activate Mesa");

            Set<Integer> otherTurns = new HashSet<>();
            var nextTurn = game.until(input -> {
                int turn = input.state().get("turn").getAsInt();
                if (input.seat() != 0 || !priority(input)) return false;
                if (turn < 5) {
                    assertEquals(ignored(input.state()).size(), 1, "The choice survives the current turn and every opponent's turn");
                    assertTrue(ability(input, "Arid Mesa").get("ignoredUntilNextTurn").getAsBoolean());
                    assertTrue(input.prompt().get("canAutoPass").getAsBoolean());
                    if (turn > 1) otherTurns.add(turn);
                    return false;
                }
                return input.state().get("activePlayerId").equals(input.state().get("viewerId"));
            });
            assertEquals(otherTurns, Set.of(2, 3, 4));
            assertEquals(nextTurn.state().get("turn").getAsInt(), 5);
            assertTrue(ignored(nextTurn.state()).isEmpty());
            assertFalse(ability(nextTurn, "Arid Mesa").get("ignoredUntilNextTurn").getAsBoolean());
            assertFalse(nextTurn.prompt().get("canAutoPass").getAsBoolean(), "Mesa stops again before any action on the next own turn");
        }
    }

    @Test(timeOut = 90000)
    public void undoWorksWhileAnotherSeatHasPriorityWithoutAViewerPrompt() throws Exception {
        try (var game = scenario("ability-ignore-undo", List.of("Arid Mesa"), "Darksteel Colossus")) {
            var main = main(game);
            var mesa = ability(main, "Arid Mesa");
            var changed = setIgnored(game, main, mesa, true);
            assertTrue(game.answer(changed, Map.of("action", "passIfNoResponse")));
            game.until(input -> input.seat() != 0 && priority(input));
            var withoutPrompt = game.state(0);
            assertTrue(!withoutPrompt.has("prompt") || withoutPrompt.get("prompt").isJsonNull());
            assertEquals(ignored(withoutPrompt).size(), 1);
            assertTrue(ignored(game.state(1)).isEmpty(), "Another seat never inherits the viewer's stop preference");
            var request = request(withoutPrompt, null, text(mesa, "id"), false);
            game.table.seat(0).action(request);
            assertTrue(ignored(game.state(0)).isEmpty());
            var returned = game.until(input -> input.seat() == 0 && priority(input));
            assertFalse(returned.prompt().get("canAutoPass").getAsBoolean());
            assertFalse(ability(returned, "Arid Mesa").get("ignoredUntilNextTurn").getAsBoolean());
        }
    }

    @DataProvider public Object[][] otherPlays() {
        return new Object[][] {{"land", "Forest"}, {"spell", "Ornithopter"}};
    }

    @Test(dataProvider = "otherPlays", timeOut = 90000)
    public void anIgnoredFetchDoesNotHideALegalLandOrSpell(String name, String mainCard) throws Exception {
        try (var game = scenario("ability-ignore-other-" + name, List.of("Arid Mesa"), mainCard)) {
            var main = main(game);
            var changed = setIgnored(game, main, ability(main, "Arid Mesa"), true);
            assertFalse(changed.prompt().get("canAutoPass").getAsBoolean(), "A playable " + name + " still requires a decision");
            game.rejected(changed, "passIfNoResponse", "This response window needs your decision");
        }
    }

    @Test(timeOut = 90000)
    public void ignoringOneMesaDoesNotIgnoreAnotherCopy() throws Exception {
        try (var game = scenario("ability-ignore-one-copy", List.of("Arid Mesa", "Arid Mesa"), "Darksteel Colossus")) {
            var main = main(game);
            var mesas = abilities(main).stream().filter(a -> text(a, "sourceName").equals("Arid Mesa")).toList();
            assertEquals(mesas.size(), 2);
            assertNotEquals(text(mesas.get(0), "id"), text(mesas.get(1), "id"));
            assertNotEquals(text(mesas.get(0), "sourceCardKey"), text(mesas.get(1), "sourceCardKey"));
            var changed = setIgnored(game, main, mesas.get(0), true);
            assertFalse(changed.prompt().get("canAutoPass").getAsBoolean(), "The other permanent's activation still stops Auto");
            assertFalse(abilities(changed).stream().filter(a -> text(a, "id").equals(text(mesas.get(1), "id"))).findFirst().orElseThrow()
                    .get("ignoredUntilNextTurn").getAsBoolean());
            changed = setIgnored(game, changed, mesas.get(1), true);
            assertTrue(changed.prompt().get("canAutoPass").getAsBoolean());
            assertEquals(ignored(changed.state()).size(), 2);
        }
    }

    @Test(timeOut = 90000)
    public void ignoringOneAbilityDoesNotIgnoreOtherAbilitiesOnThatPermanent() throws Exception {
        try (var game = scenario("ability-ignore-one-activation", List.of("Staff of Domination", "Wastes", "Wastes"), "Darksteel Colossus")) {
            var main = main(game);
            var staff = abilities(main).stream().filter(a -> text(a, "sourceName").equals("Staff of Domination")).toList();
            assertTrue(staff.size() >= 2, "Two mana permits both the Staff's untap and life-gain activations: " + staff);
            assertEquals(staff.stream().map(a -> text(a, "sourceCardKey")).distinct().count(), 1L);
            assertEquals(staff.stream().map(a -> text(a, "id")).distinct().count(), (long) staff.size());
            var changed = setIgnored(game, main, staff.get(0), true);
            assertFalse(changed.prompt().get("canAutoPass").getAsBoolean(), "Other activations on the same source still stop Auto");
            for (int index = 1; index < staff.size(); index++) changed = setIgnored(game, changed, staff.get(index), true);
            assertTrue(changed.prompt().get("canAutoPass").getAsBoolean(), "Mana sources alone do not require a priority stop");
        }
    }

    @Test(timeOut = 90000)
    public void ignoredMesaCanStillBeActivatedNormallyAndLeavesNoStalePreference() throws Exception {
        try (var game = scenario("ability-ignore-manual-activation", List.of("Arid Mesa"), "Darksteel Colossus")) {
            var main = main(game);
            var mesa = ability(main, "Arid Mesa");
            var changed = setIgnored(game, main, mesa, true);
            assertTrue(game.answer(changed, Map.of("action", "card", "key", text(mesa, "sourceCardKey"))));
            var activated = game.until(input -> input.seat() == 0 && priority(input) && !input.state().getAsJsonArray("stack").isEmpty());
            var owner = player(activated.state(), main.state().get("viewerId").getAsInt());
            assertEquals(owner.get("life").getAsInt(), 39);
            assertFalse(zone(owner, "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(card -> text(card.getAsJsonObject(), "name").equals("Arid Mesa")));
            assertTrue(zone(owner, "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(card -> text(card.getAsJsonObject(), "name").equals("Arid Mesa")));
            assertTrue(ignored(game.state(0)).isEmpty(), "The next priority scan removes a sacrificed permanent's stop preference");
        }
    }

    @Test(timeOut = 90000)
    public void invalidStaleAndOtherSeatPreferencesCannotChangeTheDecision() throws Exception {
        try (var game = scenario("ability-ignore-authorization", List.of("Arid Mesa"), "Darksteel Colossus")) {
            var main = main(game);
            var mesa = ability(main, "Arid Mesa");
            var stale = request(main.state(), "stale-prompt", text(mesa, "id"), true);
            rejectedWithoutChanging(game, main, stale);
            rejectedWithoutChanging(game, main, request(main.state(), text(main.prompt(), "id"), "unknown-ability", true));
            rejectedWithoutChanging(game, main, request(main.state(), null, "unknown-ability", false));
            var malformed = request(main.state(), text(main.prompt(), "id"), text(mesa, "id"), true);
            malformed.addProperty("ignored", "true");
            rejectedWithoutChanging(game, main, malformed);
            malformed = request(main.state(), text(main.prompt(), "id"), text(mesa, "id"), true);
            malformed.remove("abilityId");
            rejectedWithoutChanging(game, main, malformed);
            var otherSeat = game.state(1);
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).action(
                    request(otherSeat, text(main.prompt(), "id"), text(mesa, "id"), true)));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).action(
                    request(otherSeat, null, text(mesa, "id"), false)));
            assertTrue(ignored(game.state(0)).isEmpty());
            assertFalse(game.state(0).getAsJsonObject("prompt").get("canAutoPass").getAsBoolean());
        }
    }

    @Test(timeOut = 90000)
    public void anAbilityPreferenceCannotAnswerOrBypassAMulliganDecision() throws Exception {
        try (var game = scenario("ability-ignore-mulligan", List.of("Arid Mesa"), "Darksteel Colossus")) {
            var mulligan = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").contains("Mulligan"));
            assertFalse(mulligan.prompt().get("canAutoPass").getAsBoolean());
            rejectedWithoutChanging(game, mulligan, request(mulligan.state(), text(mulligan.prompt(), "id"), "unpublished-ability", true));
            assertEquals(text(game.state(0).getAsJsonObject("prompt"), "inputType"), text(mulligan.prompt(), "inputType"));
        }
    }

    private static RulesScenario scenario(String name, List<String> permanents, String mainCard) throws Exception {
        var viewer = new ArrayList<>(permanents);
        viewer.add("Spellbook");
        return new RulesScenario(name, Map.of(0, viewer, 1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")),
                mainCard, "Progenitus");
    }

    private static RulesScenario.Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }

    private static boolean priority(RulesScenario.Input input) {
        return text(input.prompt(), "inputType").equals("InputPassPriority");
    }

    private static List<JsonObject> abilities(RulesScenario.Input input) {
        assertTrue(input.prompt().has("priorityAbilities"), "Priority exposes the abilities that can interrupt Auto");
        return input.prompt().getAsJsonArray("priorityAbilities").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }

    private static JsonObject ability(RulesScenario.Input input, String name) {
        var result = abilities(input).stream().filter(value -> text(value, "sourceName").equals(name)).findFirst().orElseThrow();
        assertFalse(text(result, "id").isEmpty());
        assertFalse(text(result, "sourceCardKey").isEmpty());
        assertFalse(text(result, "text").isEmpty(), "The player gets a description of the exact activation being ignored");
        return result;
    }

    private static List<JsonObject> ignored(JsonObject state) {
        assertTrue(state.has("ignoredPriorityAbilities"), "Ignored activations remain visible outside the viewer's priority window");
        return state.getAsJsonArray("ignoredPriorityAbilities").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }

    private static RulesScenario.Input setIgnored(RulesScenario game, RulesScenario.Input input, JsonObject ability, boolean ignored) {
        game.table.seat(input.seat()).action(request(input.state(), text(input.prompt(), "id"), text(ability, "id"), ignored));
        var state = game.state(input.seat());
        assertNotNull(state.getAsJsonObject("prompt"), "A stop preference does not consume priority");
        assertEquals(text(state.getAsJsonObject("prompt"), "inputType"), "InputPassPriority");
        return new RulesScenario.Input(input.seat(), state, state.getAsJsonObject("prompt"));
    }

    private static JsonObject request(JsonObject state, String promptId, String abilityId, boolean ignored) {
        var request = new JsonObject();
        request.addProperty("sessionId", text(state, "id"));
        if (promptId != null) request.addProperty("promptId", promptId);
        request.addProperty("action", "setAbilityAutoPass");
        request.addProperty("abilityId", abilityId);
        request.addProperty("ignored", ignored);
        return request;
    }

    private static void rejectedWithoutChanging(RulesScenario game, RulesScenario.Input input, JsonObject request) {
        assertThrows(IllegalArgumentException.class, () -> game.table.seat(input.seat()).action(request));
        var after = game.state(input.seat());
        assertEquals(text(after.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
        assertTrue(ignored(after).isEmpty());
        assertFalse(after.getAsJsonObject("prompt").get("canAutoPass").getAsBoolean());
    }
}
