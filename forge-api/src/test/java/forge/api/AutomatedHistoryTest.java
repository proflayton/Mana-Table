package forge.api;

import com.google.gson.*;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** One existing ledger per actual host-owned audience; historical data grants no input capability. */
public class AutomatedHistoryTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static MatchSetupSpecification cooperative() {
        return new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE, List.of(
                new MatchSetupSpecification.Seat(0, 17, "party"), new MatchSetupSpecification.Seat(1, 17, "party"),
                new MatchSetupSpecification.Seat(2, 42, "opposition"), new MatchSetupSpecification.Seat(3, 42, "opposition")));
    }
    private static JsonObject query(String game, int seat, long cursor, int limit) {
        return object(Map.of("gameId", game, "seatId", seat, "afterSequence", cursor, "limit", limit));
    }
    private static JsonObject history(ConfiguredSimulationTest.Fixture fixture, JsonObject request) throws Exception {
        return object(fixture.engine.dispatch("automatedMatchHistory", request));
    }

    @Test(groups = "canonical-interaction", timeOut = 45000)
    public void readsTheSameBoundedAudienceLedgerAndRejectsRetiredOrUnownedScopes() throws Exception {
        try (var fixture = new ConfiguredSimulationTest.Fixture()) {
            var accepted = fixture.start(fixture.request(cooperative()));
            var bindings = accepted.getAsJsonArray("participants");
            String game = text(accepted.getAsJsonObject("state"), "id");
            int actor = bindings.get(0).getAsJsonObject().get("playerId").getAsInt();
            int ally = bindings.get(1).getAsJsonObject().get("playerId").getAsInt();
            var observation = fixture.main(actor);
            var request = query(game, actor, 0, 2);
            var first = history(fixture, request);
            assertEquals(first.get("viewerId").getAsInt(), actor);
            assertEquals(text(first, "gameId"), game);
            assertTrue(first.getAsJsonArray("records").size() <= 2);
            assertEquals(first, object(fixture.created.get().automatedSeat(actor).history(request)),
                    "The new route reads the session's exact existing ledger, not a reconstructed payment log");
            var allyHistory = history(fixture, query(game, ally, 0, 128));
            assertEquals(allyHistory.get("viewerId").getAsInt(), ally);
            for (var record : allyHistory.getAsJsonArray("records")) {
                var item = record.getAsJsonObject();
                if (item.has("payment") && !item.get("payment").isJsonNull()
                        && text(item, "exposure").equals("seatPrivate"))
                    assertEquals(item.getAsJsonObject("payment").get("payerId").getAsInt(), ally);
            }
            String before = JSON.toJson(observation.getAsJsonObject("state"));
            var all = history(fixture, query(game, actor, 0, 128));
            long last = all.get("lastSequence").getAsLong(), next = all.get("nextSequence").getAsLong();
            var tail = history(fixture, query(game, actor, next, 128));
            assertTrue(tail.getAsJsonArray("records").asList().stream()
                    .allMatch(item -> item.getAsJsonObject().get("sequence").getAsLong() > next));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query("other-game", actor, 0, 128)));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, Integer.MAX_VALUE, 0, 128)));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, actor, -1, 128)));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, actor, last + 1_000_000, 128)));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, actor, 0, 129)));
            for (JsonElement invalid : List.of(new JsonPrimitive("0"), new JsonPrimitive(0.5), JsonNull.INSTANCE)) {
                var malformed = query(game, actor, 0, 128); malformed.add("seatId", invalid);
                assertThrows(IllegalArgumentException.class, () -> history(fixture, malformed));
            }
            var after = fixture.observations().asList().stream().map(JsonElement::getAsJsonObject)
                    .filter(item -> item.get("seatId").getAsInt() == actor).findFirst().orElseThrow();
            assertEquals(JSON.toJson(after.getAsJsonObject("state")), before,
                    "History and rejected queries do not advance inputs, mutate the board or mint a new command scope");
            var replaced = fixture.start(fixture.request(cooperative()));
            assertNotEquals(text(replaced.getAsJsonObject("state"), "id"), game);
            assertThrows(IllegalArgumentException.class, () -> history(fixture, request));
            String replacement = text(replaced.getAsJsonObject("state"), "id");
            fixture.engine.dispatch("automatedSimulationStop", object(Map.of("sessionId", replacement)));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(replacement, actor, 0, 128)));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 45000)
    public void normalHostCanReadOnlyItsAutomatedOpponents() throws Exception {
        try (var fixture = new ConfiguredSimulationTest.Fixture()) {
            var setup = object(fixture.engine.dispatch("matchSetup", object(Map.of("deckId", fixture.decks.get(0).substring(5)))));
            var host = object(fixture.engine.dispatch("matchStart", object(Map.of("deckId", text(setup, "deckId"),
                    "revision", setup.get("revision").getAsLong(),
                    "opponents", List.of("green", "red")))));
            int human = host.get("viewerId").getAsInt();
            var observations = fixture.observations();
            assertEquals(observations.size(), 2, "The normal human host owns only the two external-policy opponents");
            for (var entry : observations) {
                var observation = entry.getAsJsonObject(); var state = observation.getAsJsonObject("state");
                int seat = observation.get("seatId").getAsInt(); String game = text(state, "id");
                assertNotEquals(seat, human);
                assertEquals(history(fixture, query(game, seat, 0, 128)).get("viewerId").getAsInt(), seat);
                assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, human, 0, 128)));
            }
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 45000)
    public void completedStillOwnedHistorySurvivesUntilTheActualTableRetires() throws Exception {
        try (var fixture = new ConfiguredSimulationTest.Fixture()) {
            var accepted = fixture.start(fixture.request(cooperative()));
            var bindings = accepted.getAsJsonArray("participants");
            int actor = bindings.get(0).getAsJsonObject().get("playerId").getAsInt();
            String game = text(accepted.getAsJsonObject("state"), "id");
            fixture.main(actor);
            for (int index : List.of(2, 3)) {
                int enemy = bindings.get(index).getAsJsonObject().get("playerId").getAsInt();
                var session = fixture.created.get().automatedSeat(enemy);
                session.concede(object(Map.of("sessionId", game)));
                awaitConcession(fixture.created.get(), session);
            }
            fixture.awaitPublic(state -> text(state, "status").equals("finished"));
            assertFalse(fixture.created.get().retired(), "A genuine rules result does not retire its history owner");
            assertEquals(history(fixture, query(game, actor, 0, 128)).get("viewerId").getAsInt(), actor);
            fixture.created.get().close();
            assertTrue(fixture.created.get().retired());
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, actor, 0, 128)));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 45000)
    public void faultJournalRemainsInspectableWithoutRevivingGameplayAuthority() throws Exception {
        try (var fixture = new ConfiguredSimulationTest.Fixture()) {
            var accepted = fixture.start(fixture.request(cooperative()));
            int actor = accepted.getAsJsonArray("participants").get(0).getAsJsonObject().get("playerId").getAsInt();
            String game = text(accepted.getAsJsonObject("state"), "id");
            fixture.main(actor);
            fixture.created.get().fail(new IllegalStateException("Controlled history fault"));
            assertFalse(fixture.created.get().retired(), "Fault diagnostics remain a readable owned journal");
            assertEquals(history(fixture, query(game, actor, 0, 128)).get("viewerId").getAsInt(), actor);
            fixture.engine.dispatch("automatedSimulationStop", object(Map.of("sessionId", game)));
            assertThrows(IllegalArgumentException.class, () -> history(fixture, query(game, actor, 0, 128)));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 45000)
    public void connectedRemoteSeatCannotInvokeTheHostPrivateMethod() throws Exception {
        try (var fixture = new ConfiguredSimulationTest.Fixture()) {
            fixture.engine.dispatch("multiplayerHost", object(Map.of("name", "History host")));
            var joined = object(fixture.engine.dispatch("multiplayerLocalJoin", new JsonObject()));
            var remote = query("unknown-game", 0, 0, 128);
            remote.addProperty("connectionId", text(joined, "connectionId"));
            try {
                fixture.engine.dispatch("automatedMatchHistory", remote);
                fail("A connected remote seat cannot acquire the host-private history route");
            } catch (IllegalArgumentException rejected) {
                assertEquals(rejected.getMessage(), "Command is not available on a seat connection");
            }
        }
    }
}
