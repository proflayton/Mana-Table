package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real target selection, counter effects and optional Proliferate selection share no card-name UI inference. */
public class TargetCounterExperienceTest {
    private static final Gson JSON = new Gson();
    private static final String MARE = "Loch Mare";

    @DataProvider public Object[][] targetSeats() { return new Object[][] {{0}, {1}}; }

    @Test(groups = "canonical-interaction", dataProvider = "targetSeats", timeOut = 90000)
    public void addingACounterSelectsTheExactOwnOrOpposingCreature(int targetSeat) throws Exception {
        try (var game = new RulesScenario("counter-target-add-" + targetSeat,
                Map.of(0, List.of("Forest", "Grizzly Bears"), 1, List.of("Grizzly Bears")),
                "Battlegrowth", "Rhys the Redeemed")) {
            var input = floatMana(game, main(game), "Forest", "G", 1);
            start(game, input, hand(input.state()), "Put a +1/+1 counter");
            var target = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectTargets"));
            assertEquals(text(target.prompt(), "task"), "selection");
            var chosen = card(target.state(), targetSeat, "Grizzly Bears");
            assertTrue(chosen.get("selectable").getAsBoolean(), "The current target input publishes this real creature");
            assertEquals(target.prompt().get("min").getAsInt(), 1);
            assertEquals(target.prompt().get("max").getAsInt(), 1);
            Files.writeString(game.evidence.resolve("target.json"), JSON.toJson(target.state()));
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(chosen, "key"))));
            finishPayment(game);
            var resolved = resolved(game);
            var result = card(resolved.state(), targetSeat, "Grizzly Bears");
            assertEquals(result.getAsJsonObject("counters").get("+1/+1").getAsInt(), 1);
            assertEquals(result.get("power").getAsInt(), 3);
            assertEquals(result.get("toughness").getAsInt(), 3);
            assertTrue(card(resolved.state(), 1 - targetSeat, "Grizzly Bears").getAsJsonObject("counters").isEmpty());
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(target,
                    Map.of("action", "card", "key", text(chosen, "key")))));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void removingACounterTargetsTheOtherPlayersPermanentAndPaysTheActualSourceCost() throws Exception {
        try (var game = new RulesScenario("counter-target-remove", Map.of(0,
                List.of("Concordant Crossroads", "Thrull Parasite"), 1, List.of(MARE)),
                "Island", "Rhys the Redeemed", true)) {
            var input = main(game);
            int life = viewer(input.state()).get("life").getAsInt();
            start(game, input, card(input.state(), 0, "Thrull Parasite"), "Remove a counter");
            var target = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectTargets"));
            var chosen = card(target.state(), 1, MARE);
            assertTrue(chosen.get("selectable").getAsBoolean());
            Files.writeString(game.evidence.resolve("target.json"), JSON.toJson(target.state()));
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(chosen, "key"))));
            var resolved = resolved(game);
            assertEquals(card(resolved.state(), 1, MARE).getAsJsonObject("counters").get("-1/-1").getAsInt(), 2);
            assertEquals(card(resolved.state(), 1, MARE).get("power").getAsInt(), 2);
            assertEquals(viewer(resolved.state()).get("life").getAsInt(), life - 2);
            assertTrue(card(resolved.state(), 0, "Thrull Parasite").get("tapped").getAsBoolean());
        }
    }

    @DataProvider public Object[][] proliferation() { return new Object[][] {{false}, {true}}; }

    @Test(groups = "canonical-interaction", dataProvider = "proliferation", timeOut = 90000)
    public void proliferatePreservesOptionalSelectionAndChangesOnlyTheChosenCounterBearingPermanent(boolean chooseOne) throws Exception {
        try (var game = new RulesScenario("counter-proliferate-" + chooseOne, Map.of(0,
                List.of("Island", "Island", "Island", MARE, "Grizzly Bears"), 1, List.of(MARE)),
                "Steady Progress", "Rhys the Redeemed", true)) {
            var input = floatMana(game, main(game), "Island", "U", 3);
            int handBefore = count(viewer(input.state()), "Hand");
            start(game, input, hand(input.state()), "Proliferate");
            finishPayment(game);
            var choices = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectEntitiesFromList"));
            assertTrue(card(choices.state(), 0, MARE).get("selectable").getAsBoolean());
            assertTrue(card(choices.state(), 1, MARE).get("selectable").getAsBoolean());
            assertFalse(card(choices.state(), 0, "Grizzly Bears").get("selectable").getAsBoolean());
            assertTrue(choices.prompt().get("okEnabled").getAsBoolean(), "Choosing zero objects for Proliferate is legal");
            Files.writeString(game.evidence.resolve("selection.json"), JSON.toJson(choices.state()));
            if (chooseOne) {
                var chosen = card(choices.state(), 1, MARE);
                assertTrue(game.answer(choices, Map.of("action", "card", "key", text(chosen, "key"))));
                choices = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectEntitiesFromList")
                        && card(i.state(), 1, MARE).get("highlighted").getAsBoolean());
            }
            assertTrue(game.answer(choices, Map.of("action", "ok")));
            var resolved = resolved(game);
            assertEquals(card(resolved.state(), 0, MARE).getAsJsonObject("counters").get("-1/-1").getAsInt(), 3);
            assertEquals(card(resolved.state(), 1, MARE).getAsJsonObject("counters").get("-1/-1").getAsInt(), chooseOne ? 4 : 3);
            assertEquals(count(viewer(resolved.state()), "Hand"), handBefore, "The cast consumes one card and its actual draw restores one");
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input resolved(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject hand(JsonObject state) { return zone(viewer(state), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject(); }
    private static JsonObject card(JsonObject state, int seat, String name) {
        return zone(state.getAsJsonArray("players").get(seat).getAsJsonObject(), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static void start(RulesScenario game, Input input, JsonObject source, String contains) {
        var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "text").contains(contains)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
    }
    private static Input floatMana(RulesScenario game, Input input, String land, String color, int amount) throws Exception {
        for (int mana = 1; mana <= amount; mana++) {
            var source = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals(land) && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            start(game, input, source, "Add {" + color + "}");
            int wanted = mana;
            input = game.until(i -> i.seat() == 0 && priority(i) && viewer(i.state()).getAsJsonObject("mana").get(color).getAsInt() == wanted);
        }
        return input;
    }
    private static void finishPayment(RulesScenario game) throws Exception {
        for (int step = 0; step < 8; step++) {
            var input = game.until(i -> text(i.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
            if (priority(input)) return;
            assertTrue(game.answer(input, Map.of("action", "ok")));
        }
        fail("Actual spell payment did not complete");
    }
}
