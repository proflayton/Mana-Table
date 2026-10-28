package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.cost.Cost;
import forge.game.cost.CostPart;
import forge.game.cost.CostSacrifice;
import forge.game.cost.CostTap;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Cost-order context follows actual provider parts, independently of arbitrary card/effect ordering. */
public class CostOrderingExperienceTest {
    private static final Gson JSON = new Gson();

    @Test public void onlyFullOrderingOfActualCostPartsPublishesTheProviderDefault() {
        var parts = new ArrayList<CostPart>(new Cost("T Sac<1/CARDNAME>", true).getCostPartsWithZeroMana());
        var published = DecisionSemantics.ordering(parts, true, parts.size(), parts.size());
        assertEquals(published.get("orderingPurpose"), "CostPayment");
        assertEquals(published.get("selectionIsCost"), true);
        assertEquals(published.get("defaultOrder"), List.of(0, 1, 2));
        assertTrue(parts.get(0) instanceof CostTap); assertTrue(parts.get(1) instanceof CostSacrifice);
        assertTrue(DecisionSemantics.ordering(parts.stream().map(Object::toString).toList(), true, 3, 3).isEmpty(),
                "Matching localized labels are not typed rules parts");
        assertTrue(DecisionSemantics.ordering(List.of(parts.get(0), "Sacrifice CARDNAME"), true, 2, 2).isEmpty(),
                "A mixed ordinary ordering cannot borrow purpose from one cost-like option");
        assertTrue(DecisionSemantics.ordering(parts, false, 3, 3).isEmpty());
        assertTrue(DecisionSemantics.ordering(parts, true, 0, 3).isEmpty(), "A subset choice is not the full cost ordering callback");
        assertTrue(DecisionSemantics.ordering(List.of(), true, 0, 0).isEmpty());
        parts.clear();
        assertEquals(published.get("defaultOrder"), List.of(0, 1, 2), "Published order is a copied value, not live cost objects");
    }

    @DataProvider public Object[][] costOrder() { return new Object[][] {{true}, {false}}; }

    @Test(groups = "canonical-interaction", dataProvider = "costOrder", timeOut = 90000)
    public void aRealTapThenSacrificeProducesTwoButReversingThoseResourcesCannotProduceMana(boolean useDefault) throws Exception {
        try (var game = RulesScenario.automated("cost-order-crystal-vein-" + useDefault,
                Map.of(0, List.of("Crystal Vein")), "Forest", "Rhys the Redeemed")) {
            var main = game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
            var observation = observation(game);
            var route = observation.getAsJsonObject("domain").getAsJsonArray("actions").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "effect").equals("Mana")
                            && value.get("manaAmount").getAsInt() == 2).findFirst().orElseThrow();
            var offered = observation.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> value.getAsJsonArray("actions").asList().stream()
                            .anyMatch(action -> text(action.getAsJsonObject(), "id").equals(text(route, "actionId")))).findFirst().orElseThrow();
            assertTrue(game.answer(main, Map.of("action", "useSourceAction", "key", text(offered, "sourceCardKey"),
                    "actionId", text(route, "actionId"), "manaOutput", Map.of("colors", Map.of("C", 2)))));
            var order = game.until(i -> i.seat() == 0 && ordered(i));
            var orderObservation = observation(game);
            assertEquals(text(orderObservation.getAsJsonObject("state").getAsJsonObject("prompt"), "id"), text(order.prompt(), "id"));
            var semantics = orderObservation.getAsJsonObject("semantics");
            assertEquals(text(semantics, "orderingPurpose"), "CostPayment");
            assertTrue(semantics.get("selectionIsCost").getAsBoolean(), "Standalone activation ordering has no ambient cost stack to infer this from");
            var defaults = semantics.getAsJsonArray("defaultOrder").asList().stream().map(value -> value.getAsInt()).toList();
            var choices = order.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject()).toList();
            assertEquals(defaults, choices.stream().map(value -> value.get("index").getAsInt()).toList());
            assertEquals(order.prompt().get("min").getAsInt(), choices.size());
            assertEquals(order.prompt().get("max").getAsInt(), choices.size());
            game.table.game.getAction().withState(() -> {
                assertNull(game.table.game.costPaymentStack.peek(), "Cost ordering precedes paying its first part");
                var source = game.table.game.getPlayers().get(0).getCardsIn(ZoneType.Battlefield).getFirst();
                var actual = source.getManaAbilities().stream().filter(value -> value.getParamOrDefault("Amount", "1").equals("2"))
                        .findFirst().orElseThrow().getPayCosts().getCostPartsWithZeroMana();
                assertTrue(actual.get(0) instanceof CostTap);
                assertTrue(actual.get(1) instanceof CostSacrifice);
                assertEquals(choices.stream().map(value -> text(value, "label")).toList(), actual.stream().map(Object::toString).toList(),
                        "The published default follows the real engine's current cost parts, not a reordered interpretation of labels");
            });
            for (int seat = 1; seat < 4; seat++) {
                var other = JSON.toJsonTree(game.session(seat).automatedObservation()).getAsJsonObject();
                assertNotEquals(text(other.getAsJsonObject("semantics"), "orderingPurpose"), "CostPayment",
                        "Another seat does not receive this private ordering callback");
            }
            assertEquals(observation(game), orderObservation, "Reading cost-order metadata does not pay or reorder its parts");
            Files.writeString(game.evidence.resolve("cost-order.json"), JSON.toJson(orderObservation));
            assertTrue(game.answer(order, Map.of("choices", useDefault ? defaults : List.of(defaults.get(1), defaults.get(2), defaults.get(0)))));
            var completed = game.until(i -> i.seat() == 0 && priority(i));
            assertEquals(viewer(completed.state()).getAsJsonObject("mana").get("C").getAsInt(), useDefault ? 2 : 0,
                    "Only a genuinely paid tap-then-sacrifice produces the promised two mana");
            assertTrue(zone(viewer(completed.state()), "Battlefield").getAsJsonArray("cards").isEmpty());
            assertEquals(zone(viewer(completed.state()), "Graveyard").getAsJsonArray("cards").size(), 1);
            var executions = completed.state().getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "kind").equals("mana")).toList();
            assertEquals(executions.size(), 1);
            assertEquals(text(executions.get(0), "outcome"), useDefault ? "resolved" : "cancelled");
            String execution = text(executions.get(0), "id");
            assertEquals(completed.state().getAsJsonObject("executions").getAsJsonArray("transitions").asList().stream()
                    .map(value -> value.getAsJsonObject()).anyMatch(value -> text(value, "executionId").equals(execution)
                            && text(value, "step").equals("resolution")), useDefault,
                    "A refused source tap never resolves the mana effect");
            assertNotEquals(text(observation(game).getAsJsonObject("semantics"), "orderingPurpose"), "CostPayment",
                    "Ordering purpose ends with the exact callback that owned it");
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(order, Map.of("choices", defaults))));
        }
    }

    private static JsonObject observation(RulesScenario game) {
        return JSON.toJsonTree(game.session(0).automatedObservation()).getAsJsonObject();
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static boolean ordered(Input input) {
        return text(input.prompt(), "kind").equals("choice") && input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean();
    }
}
