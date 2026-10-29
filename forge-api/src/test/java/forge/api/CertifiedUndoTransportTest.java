package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** An actual TCP guest and local host use the same one-use rules capability. */
public class CertifiedUndoTransportTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    @DataProvider public Object[][] actors() { return new Object[][]{{0}, {1}}; }

    @Test(dataProvider = "actors", groups = "canonical-interaction", timeOut = 90000)
    public void aLocalOrRemoteSeatRestoresOneSharedBoardAndReconnectsWithoutReplay(int actorSeat) throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var evidence = Files.createTempDirectory("certified-undo-tcp-" + actorSeat);
        var host = new SyncLobby(RESOURCES, evidence.resolve("host"), true);
        var guest = new SyncLobby(RESOURCES, evidence.resolve("guest"), true);
        var trace = new ArrayList<Object>();
        try {
            var lobby = object(host.host("Commander", 2, false, "Undo fixture"));
            var address = lobby.getAsJsonArray("addresses").asList().stream().map(row -> row.getAsJsonObject())
                    .filter(row -> text(row, "label").equals("This computer")).findFirst().orElseThrow().get("url").getAsString();
            guest.join(address);
            var deck = new Deck("TCP Undo");
            deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            host.selectDeck(deck, "Commander"); guest.selectDeck(deck, "Commander");
            host.ready(true); guest.ready(true); host.start();
            var endpoints = List.of(host.match(), guest.match());
            var answered = new HashMap<Integer, String>();
            var initial = until(endpoints, actorSeat, answered, trace, states -> main(states.get(actorSeat)));
            var beforeLand = initial.get(actorSeat);
            var handCard = zone(viewer(beforeLand), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            answer(endpoints.get(actorSeat), beforeLand, Map.of("action", "card", "key", text(handCard, "key")), answered, actorSeat, trace);
            var withLand = until(endpoints, actorSeat, answered, trace, states -> main(states.get(actorSeat))
                    && !text(states.get(actorSeat).getAsJsonObject("prompt"), "id").equals(text(beforeLand.getAsJsonObject("prompt"), "id")));
            var actor = withLand.get(actorSeat);
            var land = zone(viewer(actor), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = object(endpoints.get(actorSeat).sourceActions(request(actor, Map.of("key", text(land, "key")))));
            var ability = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            answer(endpoints.get(actorSeat), actor, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(ability, "id"), "expectedTask", "priority"), answered, actorSeat, trace);
            var active = until(endpoints, actorSeat, answered, trace, states -> main(states.get(actorSeat))
                    && !text(states.get(actorSeat).getAsJsonObject("prompt"), "id").equals(text(actor.getAsJsonObject("prompt"), "id")));
            var owner = active.get(actorSeat);
            var offer = owner.getAsJsonObject("undo").getAsJsonObject("offer");
            assertNotNull(offer, "The actual network actor receives the completed mana offer");
            assertTrue(active.get(1 - actorSeat).getAsJsonObject("undo").get("offer").isJsonNull());
            var body = Map.<String, Object>of("action", "undoCertifiedAction", "expectedTask", "priority",
                    "offerId", text(offer, "id"), "actionId", text(offer, "actionId"), "undoBarrierVersion", offer.get("barrierVersion").getAsLong());
            assertThrows(IllegalArgumentException.class, () -> endpoints.get(1 - actorSeat).action(request(owner, body)));
            assertEquals(viewer(object(endpoints.get(actorSeat).state())).getAsJsonObject("mana").get("G").getAsInt(), 1);
            answer(endpoints.get(actorSeat), owner, body, answered, actorSeat, trace);
            var restored = until(endpoints, actorSeat, answered, trace, states -> main(states.get(actorSeat))
                    && !text(states.get(actorSeat).getAsJsonObject("prompt"), "id").equals(text(owner.getAsJsonObject("prompt"), "id")));
            for (var state : restored) {
                assertEquals(state.get("boardRevision"), restored.get(actorSeat).get("boardRevision"));
                assertEquals(state.getAsJsonObject("undo").get("restorationVersion").getAsLong(), 1);
                assertTrue(state.getAsJsonObject("undo").get("offer").isJsonNull());
                var publicActor = player(state, owner.get("viewerId").getAsInt());
                assertEquals(publicActor.getAsJsonObject("mana").get("G").getAsInt(), 0);
                assertFalse(zone(publicActor, "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject().get("tapped").getAsBoolean());
            }
            assertThrows(IllegalArgumentException.class, () -> endpoints.get(actorSeat).action(request(restored.get(actorSeat), body)));
            assertTrue(object(guest.reconnect()).get("matchActive").getAsBoolean());
            var rejoined = object(guest.match().state());
            assertEquals(text(rejoined, "id"), text(owner, "id"));
            assertEquals(rejoined.getAsJsonObject("undo").get("restorationVersion").getAsLong(), 1);
            assertTrue(rejoined.getAsJsonObject("undo").get("offer").isJsonNull());
        } finally {
            Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace));
            guest.close(); host.close();
        }
    }

    private static List<JsonObject> until(List<MatchEndpoint> endpoints, int actor, Map<Integer, String> answered,
                                          List<Object> trace, Predicate<List<JsonObject>> stop) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
        while (System.nanoTime() < deadline) {
            var states = endpoints.stream().map(endpoint -> object(endpoint.state())).toList();
            for (var state : states) assertNotEquals(text(state, "status"), "error", state.toString());
            if (stop.test(states)) return states;
            for (int seat = 0; seat < states.size(); seat++) {
                var state = states.get(seat);
                if (state.get("prompt").isJsonNull()) continue;
                var prompt = state.getAsJsonObject("prompt");
                if (text(prompt, "id").equals(answered.get(seat))) continue;
                Map<String, ?> body;
                if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                    body = Map.of("action", "player", "playerId", states.get(actor).get("viewerId").getAsInt());
                else if (prompt.has("okEnabled") && prompt.get("okEnabled").getAsBoolean()) body = Map.of("action", "ok");
                else throw new AssertionError("Unhandled TCP Undo fixture choice: " + prompt);
                try { answer(endpoints.get(seat), state, body, answered, seat, trace); }
                catch (IllegalArgumentException stale) {
                    if (!"That choice has changed. Use the current prompt.".equals(stale.getMessage())) throw stale;
                }
            }
            Thread.sleep(5);
        }
        throw new AssertionError("The TCP Undo journey did not reach its next boundary: " + endpoints.stream().map(MatchEndpoint::state).toList());
    }
    private static void answer(MatchEndpoint endpoint, JsonObject state, Map<String, ?> body, Map<Integer, String> answered,
                               int seat, List<Object> trace) {
        endpoint.action(request(state, body));
        answered.put(seat, text(state.getAsJsonObject("prompt"), "id"));
        trace.add(Map.of("seat", seat, "state", state, "answer", body));
        assertTrue(trace.size() <= 128, "This small network journey has bounded evidence");
    }
    private static JsonObject request(JsonObject state, Map<String, ?> body) {
        var request = object(body); request.addProperty("sessionId", text(state, "id"));
        request.addProperty("promptId", text(state.getAsJsonObject("prompt"), "id")); return request;
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static boolean main(JsonObject state) {
        return text(state, "phaseKey").equals("MAIN1") && state.get("activePlayerId").equals(state.get("viewerId"))
                && !state.get("prompt").isJsonNull() && text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority");
    }
}
