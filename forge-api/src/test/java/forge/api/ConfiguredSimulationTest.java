package forge.api;

import com.google.gson.*;
import forge.StaticData;
import forge.game.player.RegisteredPlayer;
import org.testng.annotations.Test;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Predicate;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real configured rules owners and IPC, not a cooperative policy-quality claim. */
public class ConfiguredSimulationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static JsonObject card(JsonObject state, String place, String name) {
        return zone(viewer(state), place).getAsJsonArray("cards").asList().stream().map(JsonElement::getAsJsonObject)
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static MatchSetupSpecification cooperative() {
        return new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE, List.of(
                new MatchSetupSpecification.Seat(0, 17, "party"), new MatchSetupSpecification.Seat(1, 17, "party"),
                new MatchSetupSpecification.Seat(2, 42, "opposition"), new MatchSetupSpecification.Seat(3, 42, "opposition")));
    }

    @Test(timeOut = 45000) public void realTeamsKeepPrivateResourcesAndProduceAnAuthoritativeTeamOutcome() throws Exception {
        try (var fixture = new Fixture()) {
            var request = fixture.request(cooperative()); var accepted = fixture.start(request);
            var owner = fixture.created.get(); var bindings = accepted.getAsJsonArray("participants");
            assertEquals(accepted.get("setup"), request);
            assertEquals(fixture.factoryTeams.get(), List.of(17, 17, 42, 42), "Actual RegisteredPlayer teams precede the factory");
            assertEquals(bindings.size(), 4);
            int actor = bindings.get(0).getAsJsonObject().get("playerId").getAsInt();
            int ally = bindings.get(1).getAsJsonObject().get("playerId").getAsInt();
            for (int index = 0; index < 4; index++) {
                var binding = bindings.get(index).getAsJsonObject(); assertEquals(binding.get("index").getAsInt(), index);
                var original = fixture.factoryRegistrations.get().get(index);
                assertEquals(owner.game.getRegisteredPlayers().stream().filter(player -> player.getRegisteredPlayer() == original)
                        .findFirst().orElseThrow().getId(), binding.get("playerId").getAsInt());
            }
            var main = fixture.main(actor); assertPrivateTeams(fixture, bindings);
            var state = main.getAsJsonObject("state"); var forest = card(state, "Battlefield", "Forest");
            var source = main.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream().map(JsonElement::getAsJsonObject)
                    .filter(value -> text(value, "sourceCardId").equals(text(forest, "visualId"))).findFirst().orElseThrow();
            var route = source.getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = object(Map.of("action", "useSourceAction", "key", text(source, "sourceCardKey"), "actionId", text(route, "id")));
            var foreign = fixture.command(main, command); foreign.addProperty("seatId", ally);
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedAction", foreign));
            var allyForest = zone(player(state, ally), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var borrowed = command.deepCopy(); borrowed.addProperty("key", text(allyForest, "key"));
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedAction", fixture.command(main, borrowed)));
            fixture.engine.dispatch("automatedAction", fixture.command(main, command));
            var paid = fixture.until(observation -> observation.get("seatId").getAsInt() == actor
                    && viewer(observation.getAsJsonObject("state")).getAsJsonObject("mana").get("G").getAsInt() == 1);
            var paidState = paid.getAsJsonObject("state"); assertTrue(card(paidState, "Battlefield", "Forest").get("tapped").getAsBoolean());
            for (var value : paidState.getAsJsonArray("players")) {
                var player = value.getAsJsonObject(); assertEquals(player.get("life").getAsInt(), 40);
                assertEquals(player.getAsJsonObject("mana").get("G").getAsInt(), player.get("id").getAsInt() == actor ? 1 : 0);
            }
            assertFalse(zone(player(paidState, ally), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject().get("tapped").getAsBoolean());
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedAction", fixture.command(main, command)));
            assertPrivateTeams(fixture, bindings);

            // Concessions test real rules/lifecycle, never natural AI qualification.
            int firstEnemy = bindings.get(2).getAsJsonObject().get("playerId").getAsInt();
            owner.automatedSeat(firstEnemy).concede(object(Map.of("sessionId", owner.id)));
            awaitConcession(owner, owner.automatedSeat(firstEnemy));
            var remaining = fixture.publicState(); assertEquals(text(remaining, "status"), "playing");
            assertFalse(player(remaining, ally).get("eliminated").getAsBoolean());
            int lastEnemy = bindings.get(3).getAsJsonObject().get("playerId").getAsInt();
            owner.automatedSeat(lastEnemy).concede(object(Map.of("sessionId", owner.id)));
            var finished = fixture.awaitPublic(value -> text(value, "status").equals("finished"));
            var outcome = finished.getAsJsonObject("outcome"); assertNotNull(outcome);
            assertEquals(outcome.get("gameId").getAsString(), owner.id); assertEquals(outcome.get("winningTeamIds"), JSON.toJsonTree(List.of(17)));
            assertEquals(outcome.get("winningPlayerIds"), JSON.toJsonTree(List.of(actor, ally)));
            for (var observation : fixture.observations()) assertEquals(observation.getAsJsonObject().getAsJsonObject("state").get("outcome"), outcome);
            assertNull(owner.terminalReceipt()); assertNoEncounter(finished);
            Files.writeString(fixture.evidence.resolve("configured-result.json"), JSON.toJson(Map.of("accepted", accepted, "paid", paid, "outcome", finished)));
            fixture.engine.dispatch("automatedSimulationStop", object(Map.of("sessionId", owner.id)));
            assertFalse(Files.exists(fixture.profile.resolve("encounters-v1")), "Qualification cannot award or journal player encounter progress");
        }
    }

    @Test(timeOut = 45000) public void malformedOrChangedFactoryInputsCannotRetireTheCurrentSimulation() throws Exception {
        try (var fixture = new Fixture()) {
            var request = fixture.request(cooperative()); var started = fixture.start(request);
            int actor = started.getAsJsonArray("participants").get(0).getAsJsonObject().get("playerId").getAsInt();
            fixture.main(actor); String game = text(fixture.publicState(), "id");
            var invalid = new ArrayList<JsonObject>();
            var missing = request.deepCopy(); missing.remove("specification"); invalid.add(missing);
            var absent = request.deepCopy(); absent.add("specification", JsonNull.INSTANCE); invalid.add(absent);
            var count = request.deepCopy(); count.getAsJsonArray("decks").remove(3); invalid.add(count);
            var type = request.deepCopy(); type.getAsJsonArray("decks").set(0, new JsonPrimitive(7)); invalid.add(type);
            var blank = request.deepCopy(); blank.getAsJsonArray("decks").set(0, new JsonPrimitive(" ")); invalid.add(blank);
            var oversized = request.deepCopy(); oversized.getAsJsonArray("decks").set(0, new JsonPrimitive("a".repeat(201))); invalid.add(oversized);
            for (String change : List.of("unknown", "duplicate", "negative", "role", "three-party")) {
                var value = request.deepCopy(); var setup = value.getAsJsonObject("specification"); var seats = setup.getAsJsonArray("seats");
                switch (change) {
                    case "unknown" -> setup.addProperty("rulesetId", "unsupported.v99");
                    case "duplicate" -> seats.get(1).getAsJsonObject().addProperty("index", 0);
                    case "negative" -> seats.get(1).getAsJsonObject().addProperty("teamId", -1);
                    case "role" -> seats.get(1).getAsJsonObject().addProperty("role", "unknown");
                    case "three-party" -> { seats.get(2).getAsJsonObject().addProperty("role", "party"); seats.get(2).getAsJsonObject().addProperty("teamId", 17); }
                    default -> throw new AssertionError();
                }
                invalid.add(value);
            }
            int constructions = fixture.constructions.get();
            for (var value : invalid) {
                assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedSimulationStartConfigured", value));
                assertEquals(text(fixture.publicState(), "id"), game); assertEquals(fixture.constructions.get(), constructions);
            }
            var missingDeck = request.deepCopy(); missingDeck.getAsJsonArray("decks").set(3, new JsonPrimitive("deck:" + UUID.randomUUID()));
            assertThrows(Exception.class, () -> fixture.engine.dispatch("automatedSimulationStartConfigured", missingDeck));
            assertEquals(text(fixture.publicState(), "id"), game);
            fixture.changeTeam.set(true);
            assertThrows(IllegalStateException.class, () -> fixture.engine.dispatch("automatedSimulationStartConfigured", request));
            assertTrue(fixture.created.get().game.isGameOver(), "Rejected unstarted owners are disposed");
            assertEquals(text(fixture.publicState(), "id"), game); assertEquals(text(fixture.publicState(), "status"), "playing");
            fixture.changeTeam.set(false);
            fixture.engine.dispatch("automatedSimulationStop", object(Map.of("sessionId", game)));
        }
    }

    @Test(timeOut = 45000) public void legacyFfaKeepsItsBareResponseAndOldGameCommandsCannotAffectItsReplacement() throws Exception {
        try (var fixture = new Fixture()) {
            var first = fixture.start(fixture.request(cooperative())); String oldGame = text(first.getAsJsonObject("state"), "id");
            var legacy = object(fixture.engine.dispatch("automatedSimulationStart", object(Map.of("decks", fixture.decks))));
            assertFalse(legacy.has("setup")); assertFalse(legacy.has("participants")); assertNotEquals(text(legacy, "id"), oldGame);
            assertEquals(legacy.getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject().get("teamId").getAsInt()).distinct().count(), 4L);
            assertNoEncounter(legacy);
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedSimulationStop", object(Map.of("sessionId", oldGame))));
            assertEquals(text(fixture.publicState(), "id"), text(legacy, "id"));
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedAction", object(Map.of("sessionId", oldGame,
                    "seatId", first.getAsJsonArray("participants").get(0).getAsJsonObject().get("playerId").getAsInt(), "promptId", "old", "action", "ok"))));
            var ffa = new MatchSetupSpecification(MatchSetupSpecification.FREE_FOR_ALL, List.of(
                    new MatchSetupSpecification.Seat(0, 11, "participant"), new MatchSetupSpecification.Seat(1, 22, "participant"),
                    new MatchSetupSpecification.Seat(2, 33, "participant"), new MatchSetupSpecification.Seat(3, 44, "participant")));
            var configured = fixture.start(fixture.request(ffa)); assertEquals(configured.get("setup"), fixture.request(ffa));
            assertEquals(configured.getAsJsonObject("state").getAsJsonArray("players").asList().stream()
                    .map(value -> value.getAsJsonObject().get("teamId").getAsInt()).toList(), List.of(11, 22, 33, 44));
            fixture.engine.dispatch("automatedSimulationStop", object(Map.of("sessionId", text(configured.getAsJsonObject("state"), "id"))));
            assertFalse(Files.exists(fixture.profile.resolve("encounters-v1")));
        }
    }

    @Test(timeOut = 45000) public void acceptedSlotsBindRegisteredPlayersEvenWhenTheFactoryChangesTheirGameOrder() throws Exception {
        try (var fixture = new Fixture()) {
            fixture.swapSlots.set(true);
            var accepted = fixture.start(fixture.request(cooperative())); var participants = accepted.getAsJsonArray("participants");
            int actor = participants.get(0).getAsJsonObject().get("playerId").getAsInt();
            assertNotEquals(actor, fixture.created.get().game.getRegisteredPlayers().get(0).getId(),
                    "The accepted slot must follow the exact RegisteredPlayer reference, not the game's array position");
            var main = fixture.main(actor);
            assertTrue(zone(viewer(main.getAsJsonObject("state")), "Command").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Rhys the Redeemed")));
            assertPrivateTeams(fixture, participants);
        }
    }

    private static void assertNoEncounter(JsonObject state) {
        assertTrue(state.get("partyId").isJsonNull()); assertTrue(state.get("encounterId").isJsonNull());
    }
    private static void assertPrivateTeams(Fixture fixture, JsonArray bindings) throws Exception {
        for (var value : fixture.observations()) {
            var observed = value.getAsJsonObject(); var state = observed.getAsJsonObject("state");
            int viewer = observed.get("seatId").getAsInt(); assertEquals(state.get("viewerId").getAsInt(), viewer); assertNoEncounter(state);
            for (int index = 0; index < bindings.size(); index++) {
                var player = player(state, bindings.get(index).getAsJsonObject().get("playerId").getAsInt());
                assertEquals(player.get("teamId").getAsInt(), index < 2 ? 17 : 42);
                var hand = zone(player, "Hand");
                if (player.get("id").getAsInt() == viewer) assertTrue(hand.getAsJsonArray("cards").size() >= 7);
                else assertTrue(hand.getAsJsonArray("cards").isEmpty(), "An ally's private hand remains private too");
                assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            }
        }
        var publicState = fixture.publicState(); assertEquals(publicState.get("viewerId").getAsInt(), -1); assertTrue(publicState.get("prompt").isJsonNull());
        for (var value : publicState.getAsJsonArray("players")) {
            var player = value.getAsJsonObject(); assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty());
            assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            for (var zone : player.getAsJsonArray("zones")) for (var card : zone.getAsJsonObject().getAsJsonArray("cards"))
                assertEquals(text(card.getAsJsonObject(), "key"), "");
        }
    }

    static final class Fixture implements AutoCloseable {
        final Path profile = ApiTestWorkspace.profile("configured-simulation"), evidence = ApiTestWorkspace.evidence("configured-simulation");
        final DesktopEngine engine; final List<String> decks = new ArrayList<>();
        final AtomicReference<MatchTable> created = new AtomicReference<>();
        final AtomicReference<List<Integer>> factoryTeams = new AtomicReference<>();
        final AtomicReference<List<RegisteredPlayer>> factoryRegistrations = new AtomicReference<>();
        final AtomicBoolean changeTeam = new AtomicBoolean(), swapSlots = new AtomicBoolean(); final AtomicInteger constructions = new AtomicInteger();
        final Map<Integer, String> answered = new HashMap<>();
        Fixture() throws Exception {
            if (StaticData.instance() == null) EngineResources.load(RESOURCES);
            var catalog = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
            engine = new DesktopEngine(catalog, profile.resolve("decks"), (players, resources, directory) -> {
                constructions.incrementAndGet(); factoryTeams.set(players.stream().map(RegisteredPlayer::getTeamNumber).toList());
                factoryRegistrations.set(List.copyOf(players)); if (changeTeam.get()) players.get(1).setTeamNumber(99);
                for (int index = 0; index < 2; index++) players.get(index).addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Forest")));
                var gameOrder = new ArrayList<>(players); if (swapSlots.get()) Collections.swap(gameOrder, 0, 1);
                var table = new MatchTable("Commander", gameOrder, resources, directory, true); created.set(table); return table;
            });
            var resources = DesktopEngine.class.getDeclaredField("resources"); resources.setAccessible(true); resources.set(engine, RESOURCES);
            for (var pair : List.of(List.of("Forest", "Rhys the Redeemed"), List.of("Island", "Toothy, Imaginary Friend"),
                    List.of("Mountain", "Balmor, Battlemage Captain"), List.of("Wastes", "Traxos, Scourge of Kroog"))) {
                var deck = object(engine.dispatch("import", object(Map.of("format", "Commander", "name", "Configured " + pair.get(1),
                        "text", "Deck\n99 " + pair.get(0) + "\nCommander\n1 " + pair.get(1)))));
                decks.add("deck:" + text(deck, "id"));
            }
            engine.dispatch("automatedEnable", new JsonObject());
        }
        JsonObject request(MatchSetupSpecification specification) { return object(Map.of("specification", specification, "decks", decks)); }
        JsonObject start(JsonObject request) throws Exception { answered.clear(); return object(engine.dispatch("automatedSimulationStartConfigured", request)); }
        JsonObject publicState() throws Exception { return object(engine.dispatch("automatedSimulationState", new JsonObject())); }
        JsonArray observations() throws Exception { return JSON.toJsonTree(engine.dispatch("automatedState", new JsonObject())).getAsJsonArray(); }
        JsonObject command(JsonObject observation, JsonObject answer) {
            var state = observation.getAsJsonObject("state"); var result = answer.deepCopy();
            result.addProperty("sessionId", text(state, "id")); result.addProperty("seatId", observation.get("seatId").getAsInt());
            result.addProperty("promptId", text(state.getAsJsonObject("prompt"), "id")); return result;
        }
        JsonObject main(int actor) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(20);
            do {
                for (var raw : observations()) {
                    var value = raw.getAsJsonObject(); var state = value.getAsJsonObject("state");
                    assertNotEquals(text(state, "status"), "error", state.toString());
                    if (value.get("seatId").getAsInt() == actor && text(state, "phaseKey").equals("MAIN1") && state.get("prompt").isJsonObject()) return value;
                    if (!state.get("prompt").isJsonObject()) continue; var prompt = state.getAsJsonObject("prompt");
                    int seat = value.get("seatId").getAsInt(); if (text(prompt, "id").equals(answered.get(seat))) continue;
                    var answer = object(Map.of("action", "ok"));
                    if (state.get("turn").getAsInt() == 0 && !prompt.getAsJsonArray("playerChoices").isEmpty()) {
                        answer.addProperty("action", "player"); answer.addProperty("playerId", actor);
                    } else assertTrue(prompt.get("okEnabled").getAsBoolean(), prompt.toString());
                    try { engine.dispatch("automatedAction", command(value, answer)); answered.put(seat, text(prompt, "id")); }
                    catch (IllegalArgumentException stale) { if (!"That choice has changed. Use the current prompt.".equals(stale.getMessage())) throw stale; }
                }
                Thread.sleep(3);
            } while (System.nanoTime() < deadline);
            throw new AssertionError("The configured game did not reach a real main-phase decision: " + publicState());
        }
        JsonObject until(Predicate<JsonObject> predicate) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            do { for (var value : observations()) if (predicate.test(value.getAsJsonObject())) return value.getAsJsonObject(); Thread.sleep(3); } while (System.nanoTime() < deadline);
            throw new AssertionError("The configured resource action did not publish its result");
        }
        JsonObject awaitPublic(Predicate<JsonObject> predicate) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5); JsonObject state;
            do { state = publicState(); if (predicate.test(state)) return state; Thread.sleep(3); } while (System.nanoTime() < deadline);
            throw new AssertionError("The configured rules outcome did not publish: " + state);
        }
        @Override public void close() throws Exception {
            try { Files.writeString(evidence.resolve("private-final-observations.json"), JSON.toJson(observations())); }
            finally { engine.close(); }
        }
    }
}
