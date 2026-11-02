package forge.api;

import com.google.gson.*;
import forge.StaticData;
import forge.deck.*;
import org.testng.annotations.Test;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Predicate;
import static org.testng.Assert.*;

/** Actual lobby/controllers/rules outcomes and asynchronous checkpoint boundaries. */
public class CooperativeEncounterTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static String text(JsonObject value, String key) { return value.get(key).isJsonNull() ? "" : value.get(key).getAsString(); }

    @Test(timeOut = 90000) public void setupIsHostFencedAndReconnectPreservesPublicParticipantIdentity() throws Exception {
        try (var game = new Fixture("coop-authority", 2, 2, 1, null, true)) {
            var before = object(game.host.state()); var setup = before.get("matchSetup").deepCopy();
            String consent = before.get("matchSetupConsent").getAsString();
            String identity = before.getAsJsonArray("slots").get(1).getAsJsonObject().get("participantId").getAsString();
            var same = game.specification();
            assertThrows(IllegalArgumentException.class, () -> game.guests.get(0).configureMatch(game.configure(before, same)));
            var stale = game.configure(before, same); stale.addProperty("expectedLobbyRevision", before.get("revision").getAsLong() - 1);
            assertThrows(IllegalArgumentException.class, () -> game.host.configureMatch(stale));
            var enemyHuman = new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE,
                    List.of(new MatchSetupSpecification.Seat(0, 7, "party"), new MatchSetupSpecification.Seat(1, 9, "opposition"), new MatchSetupSpecification.Seat(2, 7, "party")));
            assertThrows(IllegalArgumentException.class, () -> game.host.configureMatch(game.configure(before, enemyHuman)));
            assertEquals(object(game.host.state()).get("matchSetup"), setup);
            assertTrue(object(game.host.state()).get("canStart").getAsBoolean(), "Rejected changes preserve Ready consent");
            assertThrows(IllegalArgumentException.class, () -> game.host.ready(true));
            game.guests.get(0).reconnect();
            var reconnected = object(game.guests.get(0).state());
            assertEquals(reconnected.getAsJsonArray("slots").get(1).getAsJsonObject().get("participantId").getAsString(), identity);
            assertEquals(reconnected.get("matchSetup"), setup);
            assertEquals(reconnected.get("matchSetupConsent").getAsString(), consent);
            var changed = new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE,
                    List.of(new MatchSetupSpecification.Seat(0, 17, "party"), new MatchSetupSpecification.Seat(1, 17, "party"), new MatchSetupSpecification.Seat(2, 29, "opposition")));
            var configured = object(game.host.configureMatch(game.configure(object(game.host.state()), changed)));
            for (var row : configured.getAsJsonArray("slots")) if (!row.getAsJsonObject().get("type").getAsString().equals("AI"))
                assertFalse(row.getAsJsonObject().get("ready").getAsBoolean());
            assertNotEquals(configured.get("matchSetupConsent").getAsString(), consent);
            var staleReady = object(Map.of("tableId", configured.get("tableId").getAsString(), "matchSetupConsent", consent, "ready", true));
            assertThrows(IllegalArgumentException.class, () -> game.guests.get(0).ready(staleReady));
            game.ready(); assertTrue(object(game.host.state()).get("canStart").getAsBoolean());
            String oldTempo = object(game.host.state()).getAsJsonObject("tempo").get("revision").getAsString();
            var timed = object(game.host.configureTempo(object(Map.of("tableId", configured.get("tableId").getAsString(), "preset", "fast", "expiry", "pass"))));
            assertEquals(timed.get("matchSetupConsent"), configured.get("matchSetupConsent"));
            var mismatchedTempo = object(Map.of("tableId", timed.get("tableId").getAsString(), "matchSetupConsent", timed.get("matchSetupConsent").getAsString(),
                    "tempoRevision", oldTempo, "ready", true));
            assertThrows(IllegalArgumentException.class, () -> game.guests.get(0).ready(mismatchedTempo));
            mismatchedTempo.add("tempoRevision", timed.getAsJsonObject("tempo").get("revision")); mismatchedTempo.addProperty("matchSetupConsent", consent);
            assertThrows(IllegalArgumentException.class, () -> game.guests.get(0).ready(mismatchedTempo));
            game.ready(); assertTrue(object(game.host.state()).get("canStart").getAsBoolean(), "Ready requires both displayed policies in one transition");
            staleReady.addProperty("ready", false); staleReady.addProperty("tableId", "old-table"); game.guests.get(0).ready(staleReady);
            assertFalse(object(game.host.state()).get("canStart").getAsBoolean(), "Unready revokes consent even with stale tokens");
            var openEnemy = object(Map.of("tableId", configured.get("tableId").getAsString(), "seat", 2, "type", "OPEN"));
            assertThrows(IllegalArgumentException.class, () -> game.host.setSeat(openEnemy));
            var oldGuest = game.guests.get(0); String previous = identities(object(game.host.state())).get(1); oldGuest.close();
            var replacement = game.host.localGuest();
            try { assertNotEquals(identities(object(game.host.state())).get(1), previous, "A different enrollment cannot inherit a participant identity"); }
            finally { replacement.close(); }
            var current = object(game.host.state());
            hostAi(game.host, current, 1);
            var allied = object(game.host.state()).getAsJsonArray("slots").get(1).getAsJsonObject();
            assertEquals(text(allied, "name"), "AI ally 2"); String allyId = text(allied, "participantId");
            game.host.configure(current.get("tableId").getAsString(), "Commander", 3);
            var ordinary = object(game.host.state()).getAsJsonArray("slots").get(1).getAsJsonObject();
            assertEquals(text(ordinary, "name"), "AI opponent 2"); assertEquals(text(ordinary, "participantId"), allyId);
        }
    }

    @Test(timeOut = 120000) public void oneTwoAndFourHumansUseActualTeamsPrivateHandsAndDurableReturnRematch() throws Exception {
        for (int humans : List.of(1, 2, 4)) {
            int party = humans == 1 ? 2 : humans;
            try (var game = new Fixture("coop-party-" + humans, humans, party, humans == 1 ? 1 : 2, null, false)) {
                game.start(); game.main(); game.assertPublicTeamsAndPrivateHands();
                if (humans == 1) {
                    game.concede(game.host.match());
                    game.until(states -> !text(states.get(0), "status").equals("starting") && viewer(states.get(0)).get("eliminated").getAsBoolean());
                    assertNotEquals(text(object(game.host.match().state()), "status"), "finished", "The AI ally continues after the last human loses");
                    assertTrue(object(game.host.match().state()).get("outcome").isJsonNull());
                    game.concedeOpposition();
                } else if (humans == 2) {
                    for (var human : game.humans()) game.concede(human.match());
                } else game.concedeOpposition();
                var saved = game.saved(1);
                assertEquals(saved.get("victories").getAsInt(), humans == 2 ? 0 : 1);
                var receipt = saved.getAsJsonObject("lastReceipt");
                assertEquals(receipt.getAsJsonObject("outcome").get("ruleReason").getAsString(), humans == 1 ? "AllOpponentsLost" : "AllOpposingTeamsLost");
                var capturedSelections = new HashMap<String, String>();
                for (var row : object(game.host.state()).getAsJsonArray("slots")) capturedSelections.put(text(row.getAsJsonObject(), "participantId"),
                        row.getAsJsonObject().getAsJsonObject("deckPreview").get("selectionId").getAsString());
                for (var row : receipt.getAsJsonArray("participants")) assertEquals(text(row.getAsJsonObject(), "deckSelectionId"), capturedSelections.get(text(row.getAsJsonObject(), "participantId")));
                String encounter = saved.get("encounterId").getAsString(), partyId = saved.get("partyId").getAsString();
                var before = object(game.host.state()); var roster = identities(before);
                var returned = object(game.host.returnToLobby());
                assertFalse(returned.get("matchActive").getAsBoolean()); assertEquals(identities(returned), roster);
                assertEquals(returned.get("matchSetup"), before.get("matchSetup"));
                assertEquals(returned.getAsJsonObject("partyProgress").get("completedEncounters").getAsInt(), 1);
                game.host.returnToLobby();
                if (humans == 4) {
                    game.ready(); game.start(); game.main(); game.concedeOpposition(); var next = game.saved(2);
                    assertEquals(next.get("partyId").getAsString(), partyId); assertNotEquals(next.get("encounterId").getAsString(), encounter);
                    assertEquals(next.get("victories").getAsInt(), 2); game.host.returnToLobby();
                }
            }
        }
    }

    @Test(timeOut = 90000) public void pendingAndFailedWritesKeepTheTerminalGameUntilExplicitRetry() throws Exception {
        var profile = ApiTestWorkspace.profile("coop-save-barrier");
        var allowReceipt = new CountDownLatch(1); var writingReceipt = new CountDownLatch(1); var failCheckpoint = new AtomicBoolean(true);
        var store = new EncounterProgressStore(profile, (path, bytes) -> {
            if (path.getParent().getFileName().toString().equals("receipts")) {
                writingReceipt.countDown();
                try { if (!allowReceipt.await(5, TimeUnit.SECONDS)) throw new java.io.IOException("Controlled receipt timeout"); }
                catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new java.io.IOException(error); }
            }
            if (path.getParent().getFileName().toString().equals("parties") && failCheckpoint.get()) throw new java.io.IOException("Controlled checkpoint failure");
            EncounterProgressStore.atomicWrite(path, bytes);
        });
        try (var game = new Fixture("coop-save-barrier", 1, 1, 1, store, false)) {
            game.start(); game.main(); game.concedeOpposition(); assertTrue(writingReceipt.await(5, TimeUnit.SECONDS));
            var pending = object(game.host.state()).getAsJsonObject("partyProgress");
            assertEquals(text(pending, "saveState"), "pending"); assertEquals(pending.get("completedEncounters").getAsInt(), 0);
            assertThrows(IllegalArgumentException.class, () -> game.host.returnToLobby());
            assertThrows(IllegalStateException.class, () -> game.host.close());
            allowReceipt.countDown(); game.awaitProgress(progress -> text(progress, "saveState").equals("failed"));
            var failed = object(game.host.state()); var progress = failed.getAsJsonObject("partyProgress");
            assertTrue(failed.get("matchActive").getAsBoolean()); assertTrue(progress.get("retryAllowed").getAsBoolean());
            assertEquals(progress.get("completedEncounters").getAsInt(), 0); assertTrue(progress.get("lastReceipt").isJsonNull());
            assertThrows(IllegalArgumentException.class, () -> game.host.returnToLobby());
            assertThrows(IllegalStateException.class, () -> game.host.close());
            var retry = object(Map.of("tableId", failed.get("tableId").getAsString(), "encounterId", "stale-encounter"));
            assertThrows(IllegalArgumentException.class, () -> game.host.retryEncounterSave(retry));
            failCheckpoint.set(false); retry.addProperty("encounterId", progress.get("encounterId").getAsString());
            game.host.retryEncounterSave(retry); game.saved(1); game.host.returnToLobby();
        } finally { failCheckpoint.set(false); allowReceipt.countDown(); store.close(); }
    }

    private static List<String> identities(JsonObject state) { return state.getAsJsonArray("slots").asList().stream()
            .map(row -> row.getAsJsonObject().get("participantId").getAsString()).toList(); }
    private static void hostAi(SyncLobby host, JsonObject state, int index) { host.setSeat(object(Map.of("tableId", state.get("tableId").getAsString(), "seat", index, "type", "AI"))); }
    private static JsonObject viewer(JsonObject state) { int id = state.get("viewerId").getAsInt(); return state.getAsJsonArray("players").asList().stream()
            .map(JsonElement::getAsJsonObject).filter(player -> player.get("id").getAsInt() == id).findFirst().orElseThrow(); }

    static final class Fixture implements AutoCloseable {
        final SyncLobby host; final List<SyncLobby> guests = new ArrayList<>(); final int humanCount, partyCount, enemyCount;
        final Map<Integer, String> answered = new HashMap<>();
        Fixture(String name, int humans, int party, int opposition, EncounterProgressStore supplied, boolean network) throws Exception {
            if (StaticData.instance() == null) EngineResources.load(RESOURCES);
            humanCount = humans; partyCount = party; enemyCount = opposition;
            Path profile = ApiTestWorkspace.profile(name);
            var suppliedUsed = new AtomicBoolean();
            host = new SyncLobby(RESOURCES, profile, true, null, () -> CompletableFuture.completedFuture(Map.of()),
                    () -> CompletableFuture.completedFuture(null), () -> supplied == null || suppliedUsed.getAndSet(true) ? new EncounterProgressStore(profile) : supplied);
            var state = object(host.host("Commander", party + opposition, false, network ? "mixed" : "local"));
            for (int i = 1; i < humans; i++) {
                if (network && i == 1) {
                    var guest = new SyncLobby(RESOURCES, profile, false);
                    String address = state.getAsJsonArray("addresses").get(0).getAsJsonObject().get("url").getAsString(); guest.join(address); guests.add(guest);
                } else guests.add(host.localGuest());
            }
            host.configureMatch(configure(object(host.state()), specification()));
            for (int i = humans; i < party; i++) host.setSeat(object(Map.of("tableId", object(host.state()).get("tableId").getAsString(), "seat", i, "type", "AI")));
            var deck = new Deck("Cooperative test deck"); deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            for (var human : humans()) human.selectDeck(deck, "Commander"); ready();
        }
        List<SyncLobby> humans() { var humans = new ArrayList<SyncLobby>(); humans.add(host); humans.addAll(guests); return humans; }
        MatchSetupSpecification specification() {
            var seats = new ArrayList<MatchSetupSpecification.Seat>();
            for (int i = 0; i < partyCount + enemyCount; i++) seats.add(new MatchSetupSpecification.Seat(i, i < partyCount ? 7 : 9, i < partyCount ? "party" : "opposition"));
            return new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE, seats);
        }
        JsonObject configure(JsonObject state, MatchSetupSpecification setup) { return object(Map.of("tableId", state.get("tableId").getAsString(),
                "expectedLobbyRevision", state.get("revision").getAsLong(), "specification", setup)); }
        void ready() {
            for (var human : humans()) {
                var lobby = object(human.state()); var args = object(Map.of("tableId", lobby.get("tableId").getAsString(),
                        "matchSetupConsent", lobby.get("matchSetupConsent").getAsString(), "ready", true));
                var tempo = lobby.getAsJsonObject("tempo");
                if (tempo != null && tempo.has("revision")) args.add("tempoRevision", tempo.get("revision"));
                human.ready(args);
            }
        }
        void start() throws Exception {
            var current = object(host.state());
            host.startEncounter(object(Map.of("tableId", text(current, "tableId"), "matchSetupConsent", text(current, "matchSetupConsent"),
                    "tempoRevision", text(current.getAsJsonObject("tempo"), "revision"))));
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
            while (!object(host.state()).get("matchActive").getAsBoolean() && System.nanoTime() < deadline) Thread.sleep(5);
            assertTrue(object(host.state()).get("matchActive").getAsBoolean(), "Durable admission must finish before accessing the real game");
            answered.clear();
        }
        List<MatchEndpoint> endpoints() { var endpoints = new ArrayList<MatchEndpoint>(); for (var human : humans()) endpoints.add(human.match()); endpoints.addAll(host.automatedSeats()); return endpoints; }
        void main() throws Exception { until(states -> states.stream().anyMatch(state -> text(state, "phaseKey").equals("MAIN1") && state.get("prompt").isJsonObject())); }
        void until(Predicate<List<JsonObject>> stop) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(20);
            while (System.nanoTime() < deadline) {
                var endpoints = endpoints(); var states = endpoints.stream().map(endpoint -> object(endpoint.state())).toList();
                if (stop.test(states)) return;
                for (int i = 0; i < endpoints.size(); i++) {
                    var state = states.get(i); assertNotEquals(text(state, "status"), "error", state.toString());
                    if (!state.get("prompt").isJsonObject()) continue; var prompt = state.getAsJsonObject("prompt");
                    int viewer = state.get("viewerId").getAsInt(); if (text(prompt, "id").equals(answered.get(viewer))) continue;
                    var command = object(Map.of("sessionId", state.get("id").getAsString(), "promptId", text(prompt, "id"), "action", "ok"));
                    if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty()) {
                        command.addProperty("action", "player"); command.addProperty("playerId", states.get(0).get("viewerId").getAsInt());
                    } else assertTrue(prompt.get("okEnabled").getAsBoolean(), prompt.toString());
                    endpoints.get(i).action(command); answered.put(viewer, text(prompt, "id"));
                }
                Thread.sleep(3);
            }
            throw new AssertionError("Cooperative encounter did not reach its bounded decision boundary: " + object(host.match().state()));
        }
        void assertPublicTeamsAndPrivateHands() {
            var lobby = object(host.state()); var sides = new HashMap<String, Integer>();
            for (var row : lobby.getAsJsonArray("slots")) sides.put(row.getAsJsonObject().get("participantId").getAsString(), row.getAsJsonObject().get("teamId").getAsInt());
            for (var endpoint : endpoints()) {
                var state = object(endpoint.state()); assertEquals(text(state, "partyId"), lobby.getAsJsonObject("partyProgress").get("partyId").getAsString());
                assertFalse(text(state, "encounterId").isBlank()); int viewer = state.get("viewerId").getAsInt();
                for (var row : state.getAsJsonArray("players")) {
                    var player = row.getAsJsonObject(); assertEquals(Integer.valueOf(player.get("teamId").getAsInt()), sides.get(text(player, "participantId")));
                    if (player.get("id").getAsInt() != viewer) for (var zone : player.getAsJsonArray("zones"))
                        if (Set.of("Hand", "Library").contains(text(zone.getAsJsonObject(), "name"))) {
                            assertTrue(zone.getAsJsonObject().getAsJsonArray("cards").isEmpty());
                            assertTrue(!zone.getAsJsonObject().has("topCard") || zone.getAsJsonObject().get("topCard").isJsonNull());
                        }
                }
            }
        }
        void concede(MatchEndpoint endpoint) { var state = object(endpoint.state()); endpoint.concede(object(Map.of("sessionId", state.get("id").getAsString()))); }
        void concedeOpposition() {
            for (var endpoint : host.automatedSeats()) {
                var state = object(endpoint.state()); if (viewer(state).get("teamId").getAsInt() == 9) concede(endpoint);
            }
        }
        void awaitProgress(Predicate<JsonObject> stop) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
            do { if (stop.test(object(host.state()).getAsJsonObject("partyProgress"))) return; Thread.sleep(3); } while (System.nanoTime() < deadline);
            throw new AssertionError("Encounter checkpoint did not reach its expected phase: " + host.state());
        }
        JsonObject saved(int expected) throws Exception {
            awaitProgress(progress -> text(progress, "saveState").equals("ready") && progress.get("completedEncounters").getAsInt() == expected);
            return object(host.state()).getAsJsonObject("partyProgress");
        }
        @Override public void close() { for (var guest : guests) guest.close(); host.close(); }
    }
}
