package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual input capabilities and rules events, not an authored lifecycle trace. */
public class ExecutionJourneyTest {
    private static final Gson JSON = new Gson();
    private static final String MARE = "Loch Mare";

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void concedingDuringOwnedPaymentClearsTheDecisionAndPublishesSurvivingSeats() throws Exception {
        try (var game = new RulesScenario("execution-concede-payment", Map.of(0,
                List.of(MARE, "Island", "Island", "Island")), "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            assertFalse(execution(payment).isEmpty());
            game.table.seat(0).concede(request(payment, Map.of()));
            var departed = game.state(0);
            assertTrue(viewer(departed).get("eliminated").getAsBoolean());
            assertTrue(departed.get("prompt").isJsonNull());
            assertNotNull(departed.getAsJsonObject("executions"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(payment, Map.of("action", "ok"))));
            var surviving = game.until(i -> i.seat() != 0 && priority(i));
            assertNotEquals(surviving.state().get("viewerId").getAsInt(), departed.get("viewerId").getAsInt());
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void nestedManaResponseResolutionAndCancellationKeepOneOccurrencePerActivation() throws Exception {
        try (var game = new RulesScenario("execution-loch-mare", Map.of(0, List.of(
                MARE, "Island", "Island", "Island", "Island", "Island", "Island")),
                "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            int hand = count(viewer(main.state()), "Hand");
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            String original = execution(payment);
            assertEquals(text(payment.prompt(), "executionStep"), "cost");
            var parent = occurrence(payment.state(), original);
            assertEquals(text(parent, "kind"), "activation");
            assertEquals(text(parent, "budgetRootId"), original);
            assertEquals(text(parent, "sourceName"), MARE);
            assertEquals(text(parent, "sourceCardId"), text(permanent(payment.state(), MARE), "visualId"));
            assertEquals(parent.get("actorPlayerId"), payment.state().get("viewerId"));
            assertNotEquals(original, text(payment.prompt(), "id"));
            start(game, payment, untappedIsland(payment.state()), "Add {U}");
            payment = game.until(i -> i.seat() == 0 && mana(i) && i.prompt().get("okEnabled").getAsBoolean()
                    && tappedIslands(i.state()) == 1);
            assertEquals(execution(payment), original, "A replaced input scope still pays for the same activation");
            start(game, payment, untappedIsland(payment.state()), "Add {U}");
            // Paying the final shard completes this actual input directly;
            // there is no fabricated extra confirmation after payment.
            var stacked = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").size() == 1);
            assertEquals(text(stacked.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId"), original);
            assertEquals(counter(stacked.state(), MARE, "-1/-1"), 2);
            var manaChildren = occurrences(stacked.state()).stream().filter(value -> text(value, "parentId").equals(original)).toList();
            assertEquals(manaChildren.size(), 2);
            for (var child : manaChildren) {
                assertEquals(text(child, "kind"), "mana");
                assertEquals(text(child, "budgetRootId"), original);
                assertEquals(text(child, "outcome"), "resolved");
            }
            var counterCost = activities(stacked.state(), original).stream().filter(value -> text(value, "kind").equals("counters"))
                    .findFirst().orElseThrow();
            assertEquals(text(counterCost, "executionStep"), "cost");
            assertEquals(text(counterCost.getAsJsonObject("cause"), "id"), original);
            assertEquals(counterCost.getAsJsonObject("change").get("before").getAsInt(), 3);
            assertEquals(counterCost.getAsJsonObject("change").get("after").getAsInt(), 2);

            // A response is a new announced action, never a child guessed from
            // the unrelated pending stack top of the same physical card.
            start(game, stacked, permanent(stacked.state(), MARE), "Draw a card");
            payment = payment(game);
            String response = execution(payment);
            assertNotEquals(response, original);
            assertEquals(text(occurrence(payment.state(), response), "parentId"), "");
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var two = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").size() == 2);
            assertEquals(two.state().getAsJsonArray("stack").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "executionId")).collect(java.util.stream.Collectors.toSet()), Set.of(original, response));
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), hand + 2);
            assertEquals(counter(resolved.state(), MARE, "-1/-1"), 1);
            for (String id : List.of(original, response)) {
                assertEquals(text(occurrence(resolved.state(), id), "outcome"), "resolved");
                assertTrue(activities(resolved.state(), id).stream().anyMatch(value -> text(value, "kind").equals("draw")
                        && text(value, "executionStep").equals("resolution")));
                assertOrdered(resolved.state(), id, List.of("preparation", "choice", "cost", "stack", "resolution", "complete"));
            }

            start(game, resolved, permanent(resolved.state(), MARE), "Draw a card");
            payment = payment(game);
            String cancelled = execution(payment);
            start(game, payment, untappedIsland(payment.state()), "Add {U}");
            payment = game.until(i -> i.seat() == 0 && mana(i) && i.prompt().get("cancelEnabled").getAsBoolean()
                    && tappedIslands(i.state()) == 5);
            assertEquals(execution(payment), cancelled);
            var obsolete = payment;
            assertTrue(game.answer(payment, Map.of("action", "cancel")));
            var afterCancel = game.until(i -> i.seat() == 0 && priority(i));
            assertEquals(text(occurrence(afterCancel.state(), cancelled), "outcome"), "cancelled");
            assertEquals(counter(afterCancel.state(), MARE, "-1/-1"), 1);
            assertEquals(count(viewer(afterCancel.state()), "Hand"), hand + 2);
            assertTrue(afterCancel.state().getAsJsonArray("stack").isEmpty());
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(obsolete, Map.of("action", "ok"))));
            for (int seat = 1; seat < 4; seat++) {
                var other = game.state(seat);
                assertEquals(text(occurrence(other, original), "outcome"), "resolved");
                assertTrue(other.get("prompt").isJsonNull());
                assertFalse(other.getAsJsonObject("executions").toString().contains("sourceCardKey"));
            }
            Files.writeString(game.evidence.resolve("executions.json"), JSON.toJson(afterCancel.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void copiedActivationAndRepeatedDrawTriggersAreDistinctAndRetainTheirActualProducer() throws Exception {
        try (var game = new RulesScenario("execution-copies-draws", Map.of(0, List.of(MARE,
                "Rings of Brighthearth", "Chasm Skulker", "Island", "Island", "Island", "Island", "Island", "Island")),
                "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            int hand = count(viewer(main.state()), "Hand");
            int counters = counter(main.state(), "Chasm Skulker", "+1/+1");
            long baseline = main.state().getAsJsonObject("executions").get("lastSequence").getAsLong();
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            String original = execution(payment);
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var ringPayment = payment(game);
            String rings = execution(ringPayment);
            assertNotEquals(rings, original);
            assertEquals(text(occurrence(ringPayment.state(), rings), "parentId"), original);
            assertEquals(text(occurrence(ringPayment.state(), rings), "budgetRootId"), rings,
                    "Causal parentage does not merge independent trigger decision budgets");
            assertTrue(game.answer(ringPayment, Map.of("action", "ok")));
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), hand + 2);
            assertEquals(counter(resolved.state(), MARE, "-1/-1"), 2, "The copy did not pay the original's counter cost twice");
            assertEquals(counter(resolved.state(), "Chasm Skulker", "+1/+1"), counters + 2);
            var copied = occurrences(resolved.state()).stream().filter(value -> text(value, "sourceName").equals(MARE)
                    && text(value, "parentId").equals(rings)).toList();
            assertEquals(copied.size(), 1);
            String copy = text(copied.get(0), "id");
            assertNotEquals(copy, original);
            var draws = activities(resolved.state(), original).stream().filter(value -> text(value, "kind").equals("draw")).count()
                    + activities(resolved.state(), copy).stream().filter(value -> text(value, "kind").equals("draw")).count();
            assertEquals(draws, 2L);
            var triggers = occurrences(resolved.state()).stream().filter(value -> text(value, "sourceName").equals("Chasm Skulker")
                    && Set.of(original, copy).contains(text(value, "parentId"))).toList();
            assertEquals(triggers.size(), 2);
            assertNotEquals(text(triggers.get(0), "id"), text(triggers.get(1), "id"));
            assertEquals(triggers.stream().map(value -> text(value, "parentId")).collect(java.util.stream.Collectors.toSet()), Set.of(original, copy));
            for (var trigger : triggers) {
                String id = text(trigger, "id");
                assertEquals(text(trigger, "outcome"), "resolved");
                assertEquals(text(trigger, "budgetRootId"), id);
                assertTrue(activities(resolved.state(), id).stream().anyMatch(value -> text(value, "kind").equals("counters")));
            }
            assertTrue(resolved.state().getAsJsonObject("executions").get("lastSequence").getAsLong() > baseline);
            Files.writeString(game.evidence.resolve("executions.json"), JSON.toJson(resolved.state()));
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input payment(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && mana(i) && i.prompt().get("okEnabled").getAsBoolean());
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static boolean mana(Input input) { return text(input.prompt(), "inputType").startsWith("InputPayMana"); }
    private static String execution(Input input) {
        String id = text(input.prompt(), "executionId");
        assertFalse(id.isBlank(), "The current decision references its actual execution"); return id;
    }
    private static List<JsonObject> occurrences(JsonObject state) {
        return state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static JsonObject occurrence(JsonObject state, String id) {
        return occurrences(state).stream().filter(value -> text(value, "id").equals(id)).findFirst().orElseThrow();
    }
    private static List<JsonObject> activities(JsonObject state, String execution) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "executionId").equals(execution)).toList();
    }
    private static void assertOrdered(JsonObject state, String execution, List<String> expected) {
        var actual = state.getAsJsonObject("executions").getAsJsonArray("transitions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "executionId").equals(execution)).map(value -> text(value, "step")).toList();
        assertEquals(actual, expected);
    }
    private static JsonObject permanent(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject untappedIsland(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals("Island") && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
    }
    private static long tappedIslands(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals("Island") && value.get("tapped").getAsBoolean()).count();
    }
    private static int counter(JsonObject state, String source, String type) {
        var counters = permanent(state, source).getAsJsonObject("counters");
        return counters.has(type) ? counters.get(type).getAsInt() : 0;
    }
    private static void start(RulesScenario game, Input input, JsonObject source, String contains) {
        var actions = JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "text").contains(contains)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
    }
}
