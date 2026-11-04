package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.api.sync.SyncTransport;
import forge.card.MagicColor;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.mana.Mana;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputConfirm;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** On-demand semantics belong to exactly the ordinary connection's current audience. */
public class OwnedDecisionObservationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test public void detachedWireValuesPreserveLongRevisionFencesAboveDoublePrecision() {
        long version = (1L << 53) + 1;
        var original = new HashMap<String, Object>();
        original.put("revision", version); original.put("lastSequence", Long.MAX_VALUE);
        original.put("nested", new ArrayList<>(List.of(Map.of("version", version))));
        var detached = PublishedDecisionFacts.freezeWire(original);
        original.put("revision", 0L);
        assertEquals(((Number)detached.get("revision")).longValue(), version);
        assertEquals(((Number)detached.get("lastSequence")).longValue(), Long.MAX_VALUE);
        assertThrows(UnsupportedOperationException.class, () -> ((List<?>)detached.get("nested")).clear());
    }

    @Test(timeOut = 90000)
    @SuppressWarnings("unchecked")
    public void aHumanReadIsImmutableCurrentAndExecutesItsOfferedSourceThroughTheOrdinaryActionPath() throws Exception {
        try (var game = new RulesScenario("owned-human-current", Map.of(0, List.of("Forest")), "Manalith", "Rhys the Redeemed")) {
            var main = main(game);
            var session = game.session(0);
            var before = JSON.toJsonTree(session.state());
            var observation = session.decisionObservation();
            var frame = object(observation);
            assertEquals(frame.get("state"), before);
            assertEquals(text(frame.getAsJsonObject("domain"), "coverage"), "Complete");
            assertEquals(object(session.decisionObservation()), frame, "An unchanged read keeps the exact offered action IDs");
            assertThrows(UnsupportedOperationException.class, () -> observation.put("state", Map.of()));
            var state = (Map<String, Object>)observation.get("state");
            var players = (List<Map<String, Object>>)state.get("players");
            assertThrows(UnsupportedOperationException.class, () -> players.get(0).put("life", -1));
            var domains = (Map<String, Object>)observation.get("domain");
            assertThrows(UnsupportedOperationException.class, () -> ((List<Object>)domains.get("sources")).clear());
            assertEquals(JSON.toJsonTree(session.state()), before, "Reading never advances or republishes rules");
            assertThrows(IllegalArgumentException.class, session::automatedObservation);
            var ownState = frame.getAsJsonObject("state");
            var source = frame.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "sourceCardId").equals(
                            text(card(ownState, "Battlefield", "Forest"), "visualId"))).findFirst().orElseThrow();
            var action = source.getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = Map.of("action", "useSourceAction", "key", text(source, "sourceCardKey"), "actionId", text(action, "id"));
            assertTrue(game.answer(new Input(0, ownState, ownState.getAsJsonObject("prompt")), command));
            var paid = game.until(next -> next.seat() == 0 && priority(next.state())
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            assertTrue(card(paid.state(), "Battlefield", "Forest").get("tapped").getAsBoolean());
            assertThrows(IllegalArgumentException.class, () -> session.action(request(main, command)));
            assertTrue(paid.state().getAsJsonArray("stack").isEmpty());
            assertNull(session.state().get("error"));
        }
    }

    @Test(timeOut = 90000)
    public void sameInputUnpublishedManaAndControllerReplacementCannotProduceMixedHumanAuthority() throws Exception {
        try (var game = new RulesScenario("owned-human-superseded", Map.of(0, List.of("Forest")), "Manalith", "Rhys the Redeemed")) {
            main(game);
            var session = game.session(0);
            var actor = game.table.game.getRegisteredPlayers().get(0);
            var controller = (PlayerControllerHuman)actor.getController();
            HeadlessPlatform.query(game.table, () -> {
                var before = JSON.toJsonTree(session.state());
                var forest = actor.getCardsIn(ZoneType.Battlefield).get(0);
                actor.getManaPool().addMana(new Mana(MagicColor.GREEN, forest, forest.getManaAbilities().get(0).getManaPart(), actor));
                var superseded = object(session.decisionObservation());
                assertEquals(superseded.getAsJsonObject("state").get("boardRevision"), before.getAsJsonObject().get("boardRevision"));
                assertTrue(superseded.getAsJsonObject("state").get("prompt").isJsonNull(), "The old prompt cannot grant mixed authority");
                assertEquals(text(superseded.getAsJsonObject("domain"), "coverage"), "NotApplicable");
                assertTrue(superseded.getAsJsonObject("domain").getAsJsonArray("sources").isEmpty());
                assertTrue(superseded.getAsJsonObject("domain").getAsJsonArray("actions").isEmpty());
                game.table.publish();
                assertEquals(viewer(object(session.decisionObservation()).getAsJsonObject("state"))
                        .getAsJsonObject("mana").get("G").getAsInt(), 1);
                return null;
            });
            // Use a new publication to invalidate the warmed source cache before
            // replacing a real controller inside the ensuing discovery.
            HeadlessPlatform.query(game.table, () -> { actor.getManaPool().clearPool(false); game.table.publish(); return null; });
            var original = controller.getInputQueue().getInput();
            var confirm = new InputConfirm(controller, "Required current choice", "Continue", "Cancel", true);
            var fired = new AtomicBoolean();
            var worker = Executors.newSingleThreadExecutor();
            try (var boundary = new AutomatedObservationTest.DuringDiscovery(actor, () -> {
                try { worker.submit(() -> controller.getInputQueue().setInput(confirm)).get(3, TimeUnit.SECONDS); }
                catch (Exception failure) { throw new AssertionError(failure); }
                fired.set(true);
            })) {
                assertThrows(StaleObservationException.class, session::decisionObservation);
                assertTrue(fired.get());
            } finally { worker.shutdownNow(); }
            var current = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm"));
            var child = object(session.decisionObservation());
            assertEquals(text(child.getAsJsonObject("domain"), "coverage"), "NotApplicable");
            assertTrue(child.getAsJsonObject("domain").getAsJsonArray("sources").isEmpty());
            assertTrue(game.answer(current, Map.of("action", "ok")));
            game.until(next -> next.seat() == 0 && priority(next.state()));
            assertSame(controller.getInputQueue().getInput(), original);
            assertEquals(text(object(session.decisionObservation()).getAsJsonObject("domain"), "coverage"), "Complete");
            assertNull(session.state().get("error"));
        }
    }

    @Test(timeOut = 90000)
    public void realMixedTcpObservationUsesOnlyItsConnectedHumanAndNeverForeignArgumentsOrBotPorts() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var profile = ApiTestWorkspace.evidence("owned-human-mixed-tcp");
        var cards = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
        var documents = new DeckDocumentSession(cards, profile.resolve("saved"), new DeckPlaytests(profile.resolve("saved")));
        documents.importDeck("Owned fixture", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed", "Commander");
        var opponents = new AiDeckCatalog(cards, documents);
        String opponent = opponents.choices("Commander").stream().filter(row -> row.get("id").startsWith("deck:"))
                .findFirst().orElseThrow().get("id");
        var host = new SyncLobby(RESOURCES, profile.resolve("host"), true, opponents,
                () -> CompletableFuture.completedFuture(Map.of()), () -> CompletableFuture.completedFuture(null));
        var guest = new SyncLobby(RESOURCES, profile.resolve("guest"), false);
        try {
            var lobby = object(host.host("Commander", 4, false, "mixed"));
            assertNull(host.match().decisionObservation(), "A hosted lobby without a match is explicit absence");
            String address = lobby.getAsJsonArray("addresses").asList().stream().map(row -> row.getAsJsonObject())
                    .filter(row -> text(row, "label").equals("This computer")).findFirst().orElseThrow().get("url").getAsString();
            guest.join(address);
            var deck = new Deck("Owned fixture");
            deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            host.selectDeck(deck, "Commander"); guest.selectDeck(deck, "Commander");
            for (int seat = 2; seat < 4; seat++) host.setSeat(object(Map.of("tableId", text(lobby, "tableId"),
                    "seat", seat, "type", "AI", "opponentId", opponent)));
            host.ready(true); guest.ready(true); host.start();
            var endpoints = new ArrayList<MatchEndpoint>(List.of(host.match(), guest.match()));
            endpoints.addAll(host.automatedSeats());
            assertEquals(endpoints.size(), 4);
            var frames = untilMain(endpoints, 1);
            int guestId = frames.get(1).get("viewerId").getAsInt();
            var observation = object(guest.match().decisionObservation());
            var state = observation.getAsJsonObject("state");
            assertEquals(state.get("viewerId").getAsInt(), guestId);
            assertEquals(state, frames.get(1));
            assertEquals(text(observation.getAsJsonObject("domain"), "coverage"), "Complete");
            for (var row : state.getAsJsonArray("players")) {
                var player = row.getAsJsonObject();
                assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
                if (player.get("id").getAsInt() != guestId) assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty());
            }
            var field = SyncLobby.class.getDeclaredField("connection"); field.setAccessible(true);
            var connection = (SyncTransport.Connection)field.get(guest);
            var hostile = object(Map.of("seatId", frames.get(2).get("viewerId").getAsInt(), "sessionId", "foreign-game",
                    "viewerId", frames.get(0).get("viewerId").getAsInt()));
            assertEquals(object(connection.request("decisionObservation", hostile)), observation,
                    "Caller-supplied identities cannot replace the transport-owned audience");
            assertThrows(IllegalArgumentException.class, () -> connection.request("automatedState", hostile));
            assertThrows(IllegalArgumentException.class, () -> connection.request("automatedMatchHistory", hostile));
            assertTrue(guest.automatedSeats().isEmpty());
            assertEquals(object(guest.match().state()), frames.get(1), "The ordinary state endpoint remains the same publication");
            var ownCard = zone(viewer(state), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var prompt = state.getAsJsonObject("prompt");
            var source = observation.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "sourceCardKey").equals(text(ownCard, "key")))
                    .findFirst().orElseThrow();
            var action = source.getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = request(new Input(1, state, prompt), Map.of("action", "useSourceAction", "key", text(source, "sourceCardKey"),
                    "actionId", text(action, "id")));
            guest.match().action(command);
            untilBattlefield(endpoints, guestId);
            assertThrows(IllegalArgumentException.class, () -> guest.match().action(command));
            assertEquals(object(guest.reconnect()).get("matchActive").getAsBoolean(), true);
            assertEquals(object(guest.match().decisionObservation()).getAsJsonObject("state").get("viewerId").getAsInt(), guestId);
        } finally { guest.close(); host.close(); }
    }

    @Test public void missingCapabilityIsUnsupportedRatherThanAnEmptyObservationAndCodesApplyOnlyToReads() {
        var called = new AtomicBoolean();
        try (var connection = new SeatConnection(new SyncTransport.Connection() {
            public Object request(String method, JsonObject args) { called.set(true); return null; }
            public void close() { }
        })) {
            try { connection.decisionObservation(); fail("The old host must be explicitly unsupported"); }
            catch (SyncTransport.CodedFailure rejected) { assertEquals(rejected.code(), "unsupported-capability"); }
            assertFalse(called.get());
        }
        var query = object(Map.of("id", 12, "method", "matchDecisionObservation"));
        assertEquals(text(DesktopEngine.errorReply(query, new StaleObservationException()), "errorCode"), "observation-superseded");
        query.addProperty("method", "matchAction");
        assertFalse(DesktopEngine.errorReply(query, new StaleObservationException()).has("errorCode"));
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(next -> next.seat() == 0 && priority(next.state()));
    }
    private static boolean priority(JsonObject state) {
        return text(state, "phaseKey").equals("MAIN1") && state.get("activePlayerId").equals(state.get("viewerId"))
                && !state.get("prompt").isJsonNull() && text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority");
    }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static List<JsonObject> untilMain(List<MatchEndpoint> endpoints, int actor) throws Exception {
        var answered = new HashMap<Integer, String>();
        List<JsonObject> last = List.of();
        long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
        while (System.nanoTime() < deadline) {
            var states = endpoints.stream().map(endpoint -> object(endpoint.state())).toList();
            last = states;
            if (priority(states.get(actor))) return states;
            for (int seat = 0; seat < endpoints.size(); seat++) {
                var state = states.get(seat); assertNotEquals(text(state, "status"), "error", state.toString());
                if (state.get("prompt").isJsonNull()) continue;
                var prompt = state.getAsJsonObject("prompt");
                if (text(prompt, "id").equals(answered.get(seat))) continue;
                if (prompt.has("choices") && !prompt.getAsJsonArray("choices").isEmpty()) {
                    int count = prompt.has("min") ? prompt.get("min").getAsInt() : 1;
                    var indices = prompt.getAsJsonArray("choices").asList().stream().limit(count)
                            .map(value -> value.getAsJsonObject().get("index").getAsInt()).toList();
                    endpoints.get(seat).action(request(new Input(seat, state, prompt), Map.of("choices", indices)));
                    answered.put(seat, text(prompt, "id"));
                    continue;
                }
                if ((!prompt.has("playerChoices") || prompt.getAsJsonArray("playerChoices").isEmpty())
                        && (!prompt.has("okEnabled") || !prompt.get("okEnabled").getAsBoolean())) continue;
                Map<String, ?> reply = state.get("turn").getAsInt() == 0 && prompt.has("playerChoices")
                        && !prompt.getAsJsonArray("playerChoices").isEmpty()
                        ? Map.of("action", "player", "playerId", states.get(0).get("viewerId").getAsInt()) : Map.of("action", "ok");
                endpoints.get(seat).action(request(new Input(seat, state, prompt), reply));
                answered.put(seat, text(prompt, "id"));
            }
            Thread.sleep(5);
        }
        throw new AssertionError("Mixed own-observation setup did not reach its actual human priority: " + last);
    }
    private static void untilBattlefield(List<MatchEndpoint> endpoints, int actor) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        while (System.nanoTime() < deadline) {
            var state = object(endpoints.get(1).state());
            assertNotEquals(text(state, "status"), "error", state.toString());
            var player = state.getAsJsonArray("players").asList().stream().map(row -> row.getAsJsonObject())
                    .filter(row -> row.get("id").getAsInt() == actor).findFirst().orElseThrow();
            if (zone(player, "Battlefield").getAsJsonArray("cards").size() == 1) return;
            Thread.sleep(5);
        }
        throw new AssertionError("The ordinary TCP offered land did not reach its battlefield");
    }
}
