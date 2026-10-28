package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.cost.CostTap;
import forge.game.cost.CostTapType;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectCardsFromList;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real provider inputs keep separate tap and mana costs on separate physical resources. */
public class TapCostPaymentExperienceTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] costOrders() { return new Object[][] {{true}, {false}}; }

    @Test(groups = "canonical-interaction", dataProvider = "costOrders", timeOut = 90000)
    public void anAdditionalCreatureTapCannotConsumeItsOwnPendingSourceTap(boolean creatureFirst) throws Exception {
        try (var game = RulesScenario.automated("tap-cost-distinct-resources-" + creatureFirst,
                Map.of(0, List.of("Selesnya Evangel", "Birds of Paradise", "Grizzly Bears", "Concordant Crossroads")),
                "Forest", "Rhys the Redeemed")) {
            var main = game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
            use(game, main, "Selesnya Evangel", null);
            var order = game.until(i -> i.seat() == 0 && ordered(i));
            var choices = order.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject()).toList();
            assertEquals(choices.size(), 3, "The real ability has generic mana, its source tap, and a separate creature tap");
            int source = choices.stream().filter(value -> text(value, "label").equals("{T}")).findFirst().orElseThrow().get("index").getAsInt();
            int creature = choices.stream().filter(value -> text(value, "label").startsWith("Tap ")).findFirst().orElseThrow().get("index").getAsInt();
            int mana = choices.stream().filter(value -> value.get("index").getAsInt() != source && value.get("index").getAsInt() != creature)
                    .findFirst().orElseThrow().get("index").getAsInt();
            assertTrue(game.answer(order, Map.of("choices", creatureFirst ? List.of(creature, mana, source) : List.of(source, mana, creature))));

            boolean selectedCreature = false, activatedMana = false, reachedStack = false;
            for (int step = 0; step < 12; step++) {
                var input = game.until(i -> i.seat() == 0 && (text(i.prompt(), "inputType").equals("InputSelectCardsFromList")
                        || text(i.prompt(), "inputType").startsWith("InputPayMana") || priority(i) && !i.state().getAsJsonArray("stack").isEmpty()));
                if (priority(input)) { reachedStack = true; break; }
                if (text(input.prompt(), "inputType").equals("InputSelectCardsFromList")) {
                    assertFalse(selectedCreature, "The single separate tap cost needs exactly one selection");
                    assertFalse(card(input.state(), "Selesnya Evangel").get("selectable").getAsBoolean(),
                            "The source is reserved for its own unpaid {T}, or already tapped when that cost was paid first");
                    assertTrue(card(input.state(), "Grizzly Bears").get("selectable").getAsBoolean());
                    assertEquals(card(input.state(), "Birds of Paradise").get("selectable").getAsBoolean(), creatureFirst,
                            "The mana creature is a legal early choice, and becomes unavailable after paying mana");
                    game.table.game.getAction().withState(() -> {
                        var payer = game.table.game.getPlayers().get(0);
                        var controller = (PlayerControllerHuman)payer.getController();
                        var actual = (InputSelectCardsFromList)controller.getInputQueue().getInput();
                        var host = payer.getCardsIn(ZoneType.Battlefield).stream().filter(value -> value.getName().equals("Selesnya Evangel")).findFirst().orElseThrow();
                        assertFalse(actual.getValidChoices().contains(host));
                        assertNull(actual.getActivateAction(host), "The engine click route agrees with the published candidates");
                        var current = game.table.game.costPaymentStack.peek();
                        assertTrue(current.cost() instanceof CostTapType);
                        assertEquals(current.payment().getUnpaidCostParts().stream().anyMatch(value -> value instanceof CostTap), creatureFirst,
                                "The two real orderings exercise both pending and already-paid source taps");
                    });
                    Files.writeString(game.evidence.resolve("tap-candidates.json"), JSON.toJson(input.state()));
                    assertTrue(game.answer(input, Map.of("action", "card", "key", text(card(input.state(), "Grizzly Bears"), "key"))));
                    selectedCreature = true;
                } else if (input.prompt().get("okEnabled").getAsBoolean()) {
                    assertTrue(game.answer(input, Map.of("action", "ok")));
                } else {
                    assertFalse(activatedMana, "Only one actual mana activation is required");
                    use(game, input, "Birds of Paradise", Map.of("G", 1));
                    activatedMana = true;
                }
            }
            assertTrue(selectedCreature); assertTrue(activatedMana); assertTrue(reachedStack);
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            for (String name : List.of("Selesnya Evangel", "Birds of Paradise", "Grizzly Bears"))
                assertTrue(card(resolved.state(), name).get("tapped").getAsBoolean(), "Each distinct cost consumed " + name);
            assertEquals(viewer(resolved.state()).getAsJsonObject("mana").get("G").getAsInt(), 0);
            assertEquals(zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .filter(value -> text(value.getAsJsonObject(), "name").contains("Saproling")).count(), 1L,
                    "The paid real ability resolves exactly once");
        }
    }

    private static boolean ordered(Input input) {
        return text(input.prompt(), "kind").equals("choice") && input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean();
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static void use(RulesScenario game, Input input, String name, Map<String, Integer> output) {
        var source = card(input.state(), name);
        var actions = JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals("activate")).findFirst().orElseThrow();
        var answer = new java.util.LinkedHashMap<String, Object>(Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id")));
        if (output != null) answer.put("manaOutput", Map.of("colors", output));
        assertTrue(game.answer(input, answer));
    }
}
