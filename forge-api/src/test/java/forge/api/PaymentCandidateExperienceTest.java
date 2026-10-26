package forge.api;

import com.google.gson.Gson;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Optional payment assignments publish their own candidates rather than relying on UI guesses. */
public class PaymentCandidateExperienceTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] convokeAnswers() { return new Object[][] {{true}, {false}}; }

    @Test(groups = "canonical-interaction", dataProvider = "convokeAnswers", timeOut = 90000)
    public void convokePublishesItsCandidatesAndRetainsTheChoiceToPayWithMana(boolean useCreatures) throws Exception {
        var battlefield = new java.util.ArrayList<>(List.of("Elvish Mystic", "Elvish Mystic", "Elvish Mystic", "Elvish Mystic", "Elvish Mystic"));
        if (!useCreatures) battlefield.addAll(List.of("Forest", "Forest", "Forest", "Forest", "Forest"));
        try (var game = new RulesScenario("payment-candidates-convoke-" + useCreatures, Map.of(0, battlefield),
                "Scatter the Seeds", "Rhys the Redeemed")) {
            var input = game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
            if (!useCreatures) input = floatGreen(game, input);
            var spell = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input,
                    Map.of("key", text(spell, "key"))))).getAsJsonObject();
            var cast = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"),
                    "actionId", text(cast, "id"))));
            var convoke = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectCardsForConvokeOrImprovise"));
            Files.writeString(game.evidence.resolve("convoke-candidates.json"), JSON.toJson(convoke.state()));
            var elves = zone(viewer(convoke.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Elvish Mystic")).toList();
            assertEquals(elves.size(), 5);
            assertTrue(elves.stream().allMatch(value -> value.get("selectable").getAsBoolean()),
                    "The payment input must publish its actual optional Convoke candidates");
            if (useCreatures) {
                for (var elf : elves) {
                    String visualId = text(elf, "visualId");
                    var current = game.state(0);
                    var currentElf = zone(viewer(current), "Battlefield").getAsJsonArray("cards").asList().stream()
                            .map(value -> value.getAsJsonObject()).filter(value -> text(value, "visualId").equals(visualId)).findFirst().orElseThrow();
                    assertTrue(game.answer(new Input(0, current, current.getAsJsonObject("prompt")),
                            Map.of("action", "card", "key", text(currentElf, "key"))));
                    awaitHighlight(game, visualId);
                }
            }
            var current = game.state(0);
            assertEquals(text(current.getAsJsonObject("prompt"), "inputType"), "InputSelectCardsForConvokeOrImprovise",
                    "Convoke allocations stay in their engine payment input while every command uses its fresh scope");
            assertTrue(game.answer(new Input(0, current, current.getAsJsonObject("prompt")), Map.of("action", "ok")));
            var stack = payAndReachStack(game);
            var paid = zone(viewer(stack.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).toList();
            assertTrue(paid.stream().filter(value -> text(value, "name").equals("Elvish Mystic"))
                    .allMatch(value -> value.get("tapped").getAsBoolean() == useCreatures));
            assertEquals(viewer(stack.state()).getAsJsonObject("mana").get("G").getAsInt(), 0);
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").contains("Saproling")).count(), 3L,
                    "The actual spell resolves once using the chosen payment route");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(convoke,
                    Map.of("action", "card", "key", text(elves.get(0), "key")))));
        }
    }

    private static Input floatGreen(RulesScenario game, Input input) throws Exception {
        for (int amount = 1; amount <= 5; amount++) {
            var forest = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals("Forest") && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input, Map.of("key", text(forest, "key"))))).getAsJsonObject();
            var mana = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(mana, "id"))));
            int wanted = amount;
            input = game.until(i -> i.seat() == 0 && priority(i) && viewer(i.state()).getAsJsonObject("mana").get("G").getAsInt() == wanted);
        }
        return input;
    }

    private static void awaitHighlight(RulesScenario game, String visualId) throws Exception {
        long deadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(5);
        while (System.nanoTime() < deadline) {
            var current = game.state(0);
            if (zone(viewer(current), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(value -> text(value, "visualId").equals(visualId) && value.get("highlighted").getAsBoolean())) return;
            Thread.sleep(5);
        }
        fail("The chosen physical Convoke creature was not highlighted");
    }

    private static Input payAndReachStack(RulesScenario game) throws Exception {
        for (int step = 0; step < 8; step++) {
            var input = game.until(i -> text(i.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
            if (priority(input)) return input;
            assertTrue(game.answer(input, Map.of("action", "ok")));
        }
        throw new AssertionError("The spell's actual payment did not complete");
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
}
