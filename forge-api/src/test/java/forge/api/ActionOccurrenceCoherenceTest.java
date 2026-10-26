package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import org.testng.annotations.Test;
import org.testng.annotations.DataProvider;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** One audience cannot revoke the current occurrences published for another audience. */
public class ActionOccurrenceCoherenceTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] actors() { return new Object[][] {{0}, {1}, {2}, {3}}; }

    @Test(dataProvider = "actors", timeOut = 90000)
    public void cancellingARealCastKeepsSnapshotCatalogAndGraphOnTheSameCurrentOccurrence(int actor) throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var deck = new Deck();
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Crackling Drake"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var players = java.util.stream.IntStream.range(0, 4)
                .mapToObj(index -> MatchTable.automated(deck, "Commander", "Policy " + index)).toList();
        players.get(actor).addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Island")));
        try (var table = new MatchTable("Commander", players, resources, Files.createTempDirectory("action-occurrence-coherence"), true)) {
            table.start();
            var answered = new HashMap<Integer, String>();
            var main = until(table, answered, actor, input -> input.seat() == actor
                    && text(input.prompt(), "inputType").equals("InputPassPriority") && text(input.state(), "phaseKey").equals("MAIN1"));
            var source = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(table.automatedSeat(actor).sourceActions(request(main, Map.of("key", text(source, "key"))))).getAsJsonObject();
            var cast = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            table.automatedSeat(actor).action(request(main, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(cast, "id"))));
            answered.put(actor, text(main.prompt(), "id"));
            var payment = until(table, answered, actor, input -> input.seat() == actor && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            var island = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var mana = JSON.toJsonTree(table.automatedSeat(actor).sourceActions(request(payment, Map.of("key", text(island, "key"))))).getAsJsonObject();
            table.automatedSeat(actor).action(request(payment, Map.of("action", "useSourceAction", "key", text(island, "key"),
                    "actionId", text(mana.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
            answered.put(actor, text(payment.prompt(), "id"));
            payment = until(table, answered, actor, input -> input.seat() == actor && text(input.prompt(), "inputType").startsWith("InputPayMana")
                    && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject().get("tapped").getAsBoolean());
            assertTrue(payment.prompt().get("cancelEnabled").getAsBoolean());
            table.automatedSeat(actor).action(request(payment, Map.of("action", "cancel")));
            answered.put(actor, text(payment.prompt(), "id"));
            until(table, answered, actor, input -> input.seat() == actor && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(table, () -> null);

            var observation = JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()).getAsJsonObject();
            assertCurrentOccurrences(observation);
            for (var seat : table.automatedSeats()) seat.automatedObservation();
            table.publish();
            assertCurrentOccurrences(JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()).getAsJsonObject());
            var republished = JSON.toJsonTree(table.automatedSeat(actor).automatedObservation());
            assertEquals(JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()), republished,
                    "Repeated current observations cannot rotate another audience's occurrence identities");
            var state = republished.getAsJsonObject().getAsJsonObject("state");
            var current = new Input(actor, state, state.getAsJsonObject("prompt"));
            var returned = zone(viewer(state), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(actor).action(request(current,
                    Map.of("action", "useSourceAction", "key", text(returned, "key"), "actionId", text(cast, "id")))));
            assertEquals(JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()), republished,
                    "The old cast handle cannot be rebound to the returned card's new current key");
        }
    }

    private static void assertCurrentOccurrences(JsonObject observation) {
        var state = observation.getAsJsonObject("state");
        var cards = new HashMap<String, JsonObject>();
        for (var player : state.getAsJsonArray("players"))
            for (var zone : player.getAsJsonObject().getAsJsonArray("zones"))
                for (var card : zone.getAsJsonObject().getAsJsonArray("cards")) {
                    var occurrence = card.getAsJsonObject(); cards.put(text(occurrence, "key"), occurrence);
                }
        var currentIds = cards.values().stream().map(card -> text(card, "combatId")).toList();
        var domain = observation.getAsJsonObject("domain");
        if (!text(domain, "coverage").equals("Complete")) return;
        for (var source : domain.getAsJsonArray("sources")) {
            var offer = source.getAsJsonObject();
            var current = cards.get(text(offer, "sourceCardKey"));
            assertNotNull(current);
            assertEquals(text(offer, "sourceCardId"), text(current, "combatId"),
                    "The exact current keyed source and its offered action must share one current occurrence");
        }
        for (var action : domain.getAsJsonArray("actions"))
            for (var operation : action.getAsJsonObject().getAsJsonObject("operationGraph").getAsJsonArray("operations"))
                for (String binding : List.of("sources", "recipients"))
                    for (var entity : operation.getAsJsonObject().getAsJsonObject(binding).getAsJsonArray("entities")) {
                        var value = entity.getAsJsonObject();
                        if (!text(value, "cardId").isEmpty()) assertTrue(currentIds.contains(text(value, "cardId")),
                                "Graph entities cannot reference an occurrence absent from the same authorized snapshot: " + value);
                    }
    }

    private static Input until(MatchTable table, Map<Integer, String> answered, int actor, Predicate<Input> stop) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(45);
        while (System.nanoTime() < deadline) {
            for (var seat : table.automatedSeats()) {
                var observation = JSON.toJsonTree(seat.automatedObservation()).getAsJsonObject();
                assertCurrentOccurrences(observation);
                var state = observation.getAsJsonObject("state");
                assertNotEquals(text(state, "status"), "error");
                if (!state.has("prompt") || !state.get("prompt").isJsonObject()) continue;
                var prompt = state.getAsJsonObject("prompt");
                int owner = state.get("viewerId").getAsInt();
                if (text(prompt, "id").equals(answered.get(owner))) continue;
                var input = new Input(owner, state, prompt);
                if (stop.test(input)) return input;
                Map<String, ?> answer;
                if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                    answer = Map.of("action", "player", "playerId", actor);
                else if ((text(prompt, "inputType").equals("InputPassPriority") || text(prompt, "inputType").contains("Mulligan"))
                        && prompt.get("okEnabled").getAsBoolean()) answer = Map.of("action", "ok");
                else throw new AssertionError("Unexpected setup decision: " + prompt);
                try { seat.action(request(input, answer)); answered.put(owner, text(prompt, "id")); }
                catch (IllegalArgumentException changed) {
                    if (!changed.getMessage().equals("That choice has changed. Use the current prompt.")) throw changed;
                }
            }
            Thread.sleep(5);
        }
        throw new AssertionError("The real cast/rollback did not reach its next decision");
    }
}
