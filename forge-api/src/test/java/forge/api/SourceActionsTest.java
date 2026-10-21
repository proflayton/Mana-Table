package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The production source-action capability observes first, then invokes exactly one engine ability. */
public class SourceActionsTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void readingRealManaActionsDoesNotAnswerPriorityAndAnExplicitActionUsesItsExactSource() throws Exception {
        try (var game = new RulesScenario("source-actions-mana", Map.of(0, List.of("Concordant Crossroads", "Llanowar Elves", "Llanowar Elves")))) {
            var input = main(game);
            HeadlessPlatform.query(() -> null);
            var before = game.state(0);
            var elves = zone(viewer(before), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Llanowar Elves")).toList();
            var first = query(game, input, elves.get(0));
            assertEquals(query(game, input, elves.get(0)), first, "Repeated reads retain the same current opaque action");
            assertEquals(game.state(0), before, "Reading actions changes neither snapshot nor prompt nor mana nor source");
            assertEquals(text(first, "sourceCardId"), text(elves.get(0), "visualId"));
            var action = first.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(first.getAsJsonArray("actions").size(), 1);
            assertEquals(text(action, "kind"), "activate");
            assertTrue(text(action, "text").contains("Add {G}"), "The engine's full ability description supplies its effect");
            assertTrue(text(action, "text").contains("{T}"), "The engine supplies the actual tap cost");
            var second = query(game, input, elves.get(1));
            assertNotEquals(text(second.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"), text(action, "id"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input,
                    Map.of("action", "useSourceAction", "key", text(elves.get(1), "key"), "actionId", text(action, "id")))));
            assertEquals(game.state(0), before);
            assertTrue(game.answer(input, invoke(first, action)));
            var after = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            var current = zone(viewer(after.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).toList();
            assertTrue(current.stream().filter(card -> text(card, "visualId").equals(text(first, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean());
            assertFalse(current.stream().filter(card -> text(card, "visualId").equals(text(second, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean());
            assertTrue(after.state().getAsJsonArray("stack").isEmpty(), "A real mana ability does not create a fabricated ability chooser or stack item");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input, invoke(first, action))));
        }
    }

    @Test(timeOut = 90000)
    public void anExactNonManaActionKeepsItsLaterRequiredChoiceInsteadOfChoosingTheOtherAbility() throws Exception {
        try (var game = new RulesScenario("source-actions-placement", Map.of(0, List.of("Gaea's Touch")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Gaea's Touch");
            var actions = query(game, input, source);
            assertEquals(actions.getAsJsonArray("actions").size(), 2, "Both real abilities are available before selecting the source");
            var placement = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "text").contains("Forest")).findFirst().orElseThrow();
            assertTrue(game.answer(input, invoke(actions, placement)));
            var search = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectEntitiesFromList"));
            assertNotNull(card(search.state(), "Battlefield", "Gaea's Touch"), "The exact action never substitutes the sacrifice ability");
            assertEquals(viewer(search.state()).getAsJsonObject("mana").get("G").getAsInt(), 0);
            assertEquals(text(search.prompt().getAsJsonObject("sourceCard"), "name"), "Gaea's Touch");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).sourceActions(request(search,
                    Map.of("key", text(card(search.state(), "Battlefield", "Gaea's Touch"), "key")))));
            assertEquals(text(game.state(0).getAsJsonObject("prompt"), "id"), text(search.prompt(), "id"));
        }
    }

    @Test(timeOut = 90000)
    public void paymentUsesThePaymentInputsActualManaActionsWithoutASecondAbilityChooser() throws Exception {
        try (var game = new RulesScenario("source-actions-payment", Map.of(0, List.of("Forest")))) {
            var input = main(game);
            var commander = zone(viewer(input.state()), "Command").getAsJsonArray("cards").get(0).getAsJsonObject();
            var casts = query(game, input, commander);
            var cast = casts.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(text(cast, "kind"), "cast");
            assertTrue(game.answer(input, invoke(casts, cast)));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var forest = card(payment.state(), "Battlefield", "Forest");
            var before = game.state(0);
            var mana = query(game, payment, forest);
            assertEquals(game.state(0), before);
            var action = mana.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(text(action, "kind"), "activate");
            assertTrue(text(action, "text").contains("Add {G}"));
            assertTrue(game.answer(payment, invoke(mana, action)));
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            while (game.state(0).getAsJsonArray("stack").isEmpty() && System.nanoTime() < deadline) Thread.sleep(5);
            var paid = game.until(next -> next.seat() == 0 && priority(next) && !next.state().getAsJsonArray("stack").isEmpty());
            assertTrue(card(paid.state(), "Battlefield", "Forest").get("tapped").getAsBoolean());
            assertEquals(text(paid.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "name"), "Rhys the Redeemed");
        }
    }

    @Test(timeOut = 90000)
    public void aReadOnlySeatConnectionCanDescribeAndPlayALandButCannotQueryAnotherSeatsPrivateHandles() throws Exception {
        try (var game = new RulesScenario("source-actions-seat-routing", Map.of())) {
            var input = main(game);
            var land = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            try (var connection = SeatConnection.local(game.table.seat(0))) {
                var result = JSON.toJsonTree(connection.sourceActions(request(input, Map.of("key", text(land, "key"))))).getAsJsonObject();
                var action = result.getAsJsonArray("actions").get(0).getAsJsonObject();
                assertEquals(text(action, "kind"), "play");
                assertEquals(text(result, "sourceCardId"), text(land, "visualId"));
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).sourceActions(request(input, Map.of("key", text(land, "key")))));
                assertTrue(zone(player(game.state(1), input.state().get("viewerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty());
                connection.action(request(input, invoke(result, action)));
                var played = game.until(next -> next.seat() == 0 && priority(next) && count(viewer(next.state()), "Battlefield") == 1);
                assertEquals(viewer(played.state()).get("landPlaysUsed").getAsInt(), 1);
            }
        }
    }

    @Test(timeOut = 90000)
    public void aLateRulesMutationRevokesAnAcceptedQueuedActionAndRestoresTheUnansweredInput() throws Exception {
        try (var game = new RulesScenario("source-actions-late-revocation", Map.of(0, List.of("Sol Ring"), 1, List.of("Spellbook")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Sol Ring");
            var actions = query(game, input, source);
            var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
            var entered = new CountDownLatch(1);
            var release = new CountDownLatch(1);
            try {
                HeadlessPlatform.later(() -> { entered.countDown(); await(release); });
                assertTrue(entered.await(5, TimeUnit.SECONDS));
                assertTrue(game.answer(input, invoke(actions, action)));
                var concede = new JsonObject(); concede.addProperty("sessionId", text(input.state(), "id"));
                game.table.seat(1).concede(concede);
                release.countDown();
                HeadlessPlatform.query(() -> null);
                var restored = game.state(0);
                assertEquals(text(restored.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
                assertFalse(card(restored, "Battlefield", "Sol Ring").get("tapped").getAsBoolean());
                assertEquals(viewer(restored).getAsJsonObject("mana").get("C").getAsInt(), 0);
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input, invoke(actions, action))));
                assertTrue(query(game, new Input(0, restored, restored.getAsJsonObject("prompt")), source).getAsJsonArray("actions").size() > 0);
            } finally { release.countDown(); }
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElse(null);
    }
    private static JsonObject query(RulesScenario game, Input input, JsonObject source) {
        return JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
    }
    private static Map<String, ?> invoke(JsonObject actions, JsonObject action) {
        return Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"));
    }
    private static JsonObject request(Input input, Map<String, ?> body) {
        var request = JSON.toJsonTree(body).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id")); request.addProperty("promptId", text(input.prompt(), "id"));
        return request;
    }
    private static void await(CountDownLatch latch) {
        try { if (!latch.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input queue was not released"); }
        catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
    }
}
