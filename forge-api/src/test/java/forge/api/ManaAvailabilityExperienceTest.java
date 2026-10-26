package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;
import java.util.List;
import java.util.Map;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Compare the current playable cue with payment through the same live input. */
public class ManaAvailabilityExperienceTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] payments() {
        return new Object[][] {
                {"Ashnod's Altar", List.of("Basalt Monolith"), true},
                {"Blasting Station", List.of("Basalt Monolith"), true},
                {"Blasting Station", List.of("Wastes", "Wastes", "Wastes"), true},
                {"Blasting Station", List.of("Sol Ring", "Victory Chimes"), true},
                {"Ultima", List.of("Basalt Monolith", "Plains", "Plains"), true},
                {"Opt", List.of("Basalt Monolith"), false},
                {"Opt", List.of("Sol Ring", "Victory Chimes"), false},
                {"Endbringer", List.of("Basalt Monolith", "Forest", "Forest", "Forest"), true},
                {"Endbringer", List.of("Forest", "Forest", "Forest", "Forest", "Forest", "Forest"), false}
        };
    }

    @Test(dataProvider = "payments", timeOut = 90000)
    public void currentManaCueDistinguishesGenericColoredAndColorlessRequirements(String spell, List<String> permanents, boolean payable) throws Exception {
        try (var game = new RulesScenario("mana-availability-" + spell.replaceAll("[^A-Za-z]", ""),
                Map.of(0, permanents), spell, "Omnath, Locus of the Void")) {
            var input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            if (permanents.contains("Basalt Monolith")) {
                var monolith = card(input.state(), "Battlefield", "Basalt Monolith");
                var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input, Map.of("key", text(monolith, "key"))))).getAsJsonObject();
                var tap = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(action -> text(action, "text").contains("Add")).findFirst().orElseThrow();
                assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(tap, "id"))));
                input = game.until(next -> next.seat() == 0 && priority(next)
                        && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 3);
                assertTrue(card(input.state(), "Battlefield", "Basalt Monolith").get("tapped").getAsBoolean());
            }
            var hand = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            boolean offered = hand.get("selectable").getAsBoolean();
            if (!payable) { assertFalse(offered, "Colorless production must not satisfy a missing colored requirement"); return; }
            if (permanents.contains("Victory Chimes")) {
                assertFalse(input.prompt().get("canAutoPass").getAsBoolean(), "An unplanned manual mana route cannot authorize Auto");
                input = floatChimes(game, input);
                hand = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            }
            assertTrue(game.answer(input, Map.of("action", "card", "key", text(hand, "key"))));
            var casting = game.until(next -> next.seat() == 0 && (text(next.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(next) && stacked(next.state(), spell)));
            if (text(casting.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(game.answer(casting, Map.of("action", "ok")));
                long deadline = System.nanoTime() + java.time.Duration.ofSeconds(5).toNanos();
                JsonObject state = game.state(0);
                while (!stacked(state, spell) || state.get("prompt").isJsonNull()
                        || !text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority")) {
                    assertTrue(System.nanoTime() < deadline, "Accepted payment must reach casting priority: " + state);
                    Thread.sleep(5);
                    state = game.state(0);
                }
                casting = new Input(0, state, state.getAsJsonObject("prompt"));
            }
            assertTrue(stacked(casting.state(), spell), "The engine must accept the same payment as the playable cue");
            assertEquals(offered, payable, "A spell that the player can actually pay for must remain discoverable before manual mana production");
            assertEquals(text(casting.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "kind"), "spell");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void landfallManaIsAnnouncedAsATriggerBeforeItActuallyProducesMana() throws Exception {
        String omnath = "Omnath, Locus of the Void";
        try (var game = new RulesScenario("stack-kind-landfall-mana", Map.of(0, List.of(omnath)))) {
            var main = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var forest = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(forest, "key"))));
            var pending = game.until(next -> next.seat() == 0 && priority(next) && stacked(next.state(), omnath));
            assertEquals(text(pending.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "kind"), "triggered");
            assertEquals(viewer(pending.state()).getAsJsonObject("mana").get("C").getAsInt(), 0,
                    "The pending trigger has not produced its mana yet");
            assertTrue(game.answer(pending, Map.of("action", "ok")));
            var resolved = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 2);
            assertEquals(viewer(resolved.state()).getAsJsonObject("mana").get("C").getAsInt(), 2);
        }
    }

    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static Input floatChimes(RulesScenario game, Input input) throws Exception {
        var chimes = card(input.state(), "Battlefield", "Victory Chimes");
        var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input, Map.of("key", text(chimes, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
        var recipient = game.until(next -> next.seat() == 0 && !next.prompt().getAsJsonArray("playerChoices").isEmpty());
        assertTrue(game.answer(recipient, Map.of("action", "player", "playerId", input.state().get("viewerId").getAsInt())));
        return game.until(next -> next.seat() == 0 && priority(next) && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 1);
    }
    private static JsonObject card(JsonObject state, String location, String name) {
        return zone(viewer(state), location).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElseThrow();
    }
    private static boolean stacked(JsonObject state, String name) {
        return state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject()).anyMatch(item -> text(item, "name").equals(name));
    }
}
