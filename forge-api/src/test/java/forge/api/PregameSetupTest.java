package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.player.RegisteredPlayer;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Trusted pregame composition still launches through ordinary authenticated
 * lobby channels. These are bootstrap/lifetime tests, not a fanout qualification. */
public class PregameSetupTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(timeOut = 90000)
    public void explicitPrivateRegistrationReachesTheRealMixedTcpTableWithoutChangingItsAudienceOrBots() throws Exception {
        var captured = new AtomicReference<List<RegisteredPlayer>>();
        try (var fixture = new Fixture("polled-pregame-registration", (format, players) -> {
            assertThrows(UnsupportedOperationException.class, () -> players.add(players.get(0)));
            captured.set(players); AiReleaseFixtureEngine.configurePolledFanout(format, players);
        })) {
            fixture.host.start();
            var frames = main(fixture.endpoints());
            var host = frames.get(0); var guest = frames.get(1);
            assertEquals(zone(viewer(host), "Battlefield").getAsJsonArray("cards").size(), 61);
            assertEquals(named(host, "Memnite"), 54); assertEquals(named(host, "Toothy, Imaginary Friend"), 1);
            assertEquals(named(host, "Forest"), 5); assertEquals(named(host, "Reliquary Tower"), 1);
            assertEquals(named(guest, "Mind's Eye"), 1);
            assertEquals(zone(viewer(guest), "Battlefield").getAsJsonArray("cards").size(), 1);
            assertTrue(viewer(guest).getAsJsonObject("mana").entrySet().stream().allMatch(value -> value.getValue().getAsInt() == 0));
            assertTrue(zone(viewer(host), "Hand").getAsJsonArray("cards").asList().stream()
                    .allMatch(value -> text(value.getAsJsonObject(), "name").equals("Shamanic Revelation")));
            int hostId = host.get("viewerId").getAsInt();
            assertTrue(zone(player(guest, hostId), "Hand").getAsJsonArray("cards").isEmpty());
            assertTrue(fixture.guest.automatedSeats().isEmpty()); assertEquals(fixture.host.automatedSeats().size(), 2);
            for (int seat : List.of(2, 3)) {
                assertEquals(captured.get().get(seat).getDeck().getMain().countAll(), 99);
                assertEquals(zone(viewer(frames.get(seat)), "Battlefield").getAsJsonArray("cards").size(), 0);
            }
            assertNotEquals(captured.get().get(2).getDeck().getCommanders().get(0).getName(),
                    captured.get().get(3).getDeck().getCommanders().get(0).getName());
            assertEquals(captured.get().stream().map(value -> value.getTeamNumber()).distinct().count(), 4L,
                    "Mind's Eye must observe an opponent's draw, not a teammate's");
        }
    }

    @Test(timeOut = 90000)
    public void ordinaryDefaultSetupPreservesTheSelectedDeckAndCreatesNoFixturePermanents() throws Exception {
        try (var fixture = new Fixture("polled-pregame-default", SyncLobby.PregameSetup.NONE)) {
            fixture.host.start(); var frames = main(fixture.endpoints());
            assertEquals(zone(viewer(frames.get(0)), "Battlefield").getAsJsonArray("cards").size(), 0);
            assertTrue(zone(viewer(frames.get(0)), "Hand").getAsJsonArray("cards").asList().stream()
                    .allMatch(value -> text(value.getAsJsonObject(), "name").equals("Island")));
            assertFalse(object(fixture.host.state()).has("startState"), "An ordinary committed game is not a durable encounter attempt");
        }
    }

    @Test(timeOut = 90000)
    public void blockedSetupReleasesTheLobbyMonitorAndTheOtherTcpSeatCanRevokeThatCapturedAttempt() throws Exception {
        var entered = new CountDownLatch(1); var release = new CountDownLatch(1); var calls = new AtomicInteger();
        var workers = Executors.newFixedThreadPool(3);
        try (var fixture = new Fixture("polled-pregame-unready", (format, players) -> {
            if (calls.incrementAndGet() == 1) block(entered, release);
        })) {
            var old = workers.submit(fixture.host::start);
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            assertTrue(workers.submit(() -> { synchronized (fixture.host) { return true; } }).get(2, TimeUnit.SECONDS),
                    "The trusted callback does not own the lobby monitor; one connection's request stream remains serialized");
            assertFalse(object(workers.submit(fixture.guest::state).get(2, TimeUnit.SECONDS)).get("matchActive").getAsBoolean());
            assertNull(fixture.guest.match().state());
            fixture.guest.ready(false); release.countDown();
            assertThrows(java.util.concurrent.ExecutionException.class, () -> old.get(5, TimeUnit.SECONDS));
            assertFalse(object(fixture.host.state()).get("matchActive").getAsBoolean());
            assertTrue(fixture.host.automatedSeats().isEmpty());
            fixture.guest.ready(true); fixture.host.start();
            assertEquals(main(fixture.endpoints()).size(), 4); assertEquals(calls.get(), 2);
        } finally { release.countDown(); workers.shutdownNow(); }
    }

    @Test(timeOut = 90000)
    public void aRehostedWorldCannotBeReplacedByTheOldBlockedRoster() throws Exception {
        var entered = new CountDownLatch(1); var release = new CountDownLatch(1); var calls = new AtomicInteger();
        var workers = Executors.newFixedThreadPool(2);
        try (var fixture = new Fixture("polled-pregame-rehost", (format, players) -> {
            if (calls.incrementAndGet() == 1) block(entered, release);
        })) {
            String oldId = text(object(fixture.host.state()), "tableId");
            var old = workers.submit(fixture.host::start); assertTrue(entered.await(5, TimeUnit.SECONDS));
            var rehost = workers.submit(() -> fixture.host.host("Commander", 2, false, "local"));
            var closing = SyncLobby.class.getDeclaredField("closing"); closing.setAccessible(true);
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
            while (System.nanoTime() < deadline) {
                synchronized (fixture.host) { if (closing.getBoolean(fixture.host)) break; }
                Thread.sleep(3);
            }
            synchronized (fixture.host) { assertTrue(closing.getBoolean(fixture.host)); }
            release.countDown();
            assertThrows(java.util.concurrent.ExecutionException.class, () -> old.get(5, TimeUnit.SECONDS));
            var fresh = object(rehost.get(5, TimeUnit.SECONDS)); assertNotEquals(text(fresh, "tableId"), oldId);
            assertEquals(text(object(fixture.host.state()), "tableId"), text(fresh, "tableId"));
            assertFalse(object(fixture.host.state()).get("matchActive").getAsBoolean());
            try (var friend = new LobbyClose(fixture.host.localGuest())) {
                fixture.host.selectDeck(deck("fresh host", "Island", "Toothy, Imaginary Friend"), "Commander");
                friend.lobby.selectDeck(deck("fresh friend", "Plains", "Isamaru, Hound of Konda"), "Commander");
                fixture.host.ready(true); friend.lobby.ready(true); fixture.host.start();
                assertEquals(main(List.of(fixture.host.match(), friend.lobby.match())).size(), 2);
                assertEquals(calls.get(), 2);
            }
        } finally { release.countDown(); workers.shutdownNow(); }
    }

    @Test(timeOut = 90000)
    public void aTrustedSetupFailureLeavesNoMatchAndAFreshNormalStartStillWorks() throws Exception {
        var calls = new AtomicInteger();
        try (var fixture = new Fixture("polled-pregame-fault", (format, players) -> {
            if (calls.incrementAndGet() == 1) throw new IllegalStateException("Controlled pregame setup failure");
        })) {
            assertThrows(IllegalArgumentException.class, fixture.host::start);
            assertFalse(object(fixture.host.state()).get("matchActive").getAsBoolean());
            assertNull(fixture.guest.match().state()); assertTrue(fixture.host.automatedSeats().isEmpty());
            assertTrue(object(fixture.host.state()).get("canStart").getAsBoolean());
            fixture.host.start(); assertEquals(main(fixture.endpoints()).size(), 4); assertEquals(calls.get(), 2);
        }
    }

    @Test(timeOut = 90000)
    public void theSamePregameHookCannotCommitACancelledCooperativeReservationOverANewOrdinaryGame() throws Exception {
        var entered = new CountDownLatch(1); var release = new CountDownLatch(1); var calls = new AtomicInteger();
        try (var fixture = new Fixture("polled-pregame-cooperative-cancel", (format, players) -> {
            if (calls.incrementAndGet() == 1) block(entered, release);
        })) {
            var lobby = object(fixture.host.state());
            var setup = new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE, List.of(
                    new MatchSetupSpecification.Seat(0, 7, "party"), new MatchSetupSpecification.Seat(1, 7, "party"),
                    new MatchSetupSpecification.Seat(2, 9, "opposition"), new MatchSetupSpecification.Seat(3, 9, "opposition")));
            fixture.host.configureMatch(object(Map.of("tableId", text(lobby, "tableId"), "expectedLobbyRevision", lobby.get("revision").getAsLong(),
                    "specification", setup)));
            lobby = object(fixture.host.state());
            var ready = object(Map.of("tableId", text(lobby, "tableId"), "matchSetupConsent", text(lobby, "matchSetupConsent"), "ready", true));
            fixture.host.ready(ready); fixture.guest.ready(ready);
            var pending = object(fixture.host.startEncounter(object(Map.of("tableId", text(lobby, "tableId"),
                    "matchSetupConsent", text(lobby, "matchSetupConsent"), "tempoRevision", text(lobby.getAsJsonObject("tempo"), "revision")))));
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            assertFalse(object(fixture.guest.state()).get("matchActive").getAsBoolean());
            fixture.host.cancelEncounterStart(object(Map.of("tableId", text(pending, "tableId"),
                    "attemptId", text(pending.getAsJsonObject("startState"), "attemptId"))));
            release.countDown();
            var field = SyncLobby.class.getDeclaredField("startCoordinator"); field.setAccessible(true);
            ((java.util.concurrent.ExecutorService)field.get(fixture.host)).submit(() -> { }).get(5, TimeUnit.SECONDS);
            assertFalse(object(fixture.host.state()).get("matchActive").getAsBoolean());
            assertNull(fixture.host.match().state()); assertTrue(fixture.host.automatedSeats().isEmpty());
            fixture.host.configure(text(lobby, "tableId"), "Commander", 4);
            fixture.host.ready(true); fixture.guest.ready(true); fixture.host.start();
            var frames = main(fixture.endpoints());
            for (var state : frames) {
                assertTrue(state.get("outcome").isJsonNull()); assertTrue(state.get("encounterId").isJsonNull());
                assertTrue(state.get("error").isJsonNull());
            }
            assertEquals(calls.get(), 2);
        } finally { release.countDown(); }
    }

    private static void block(CountDownLatch entered, CountDownLatch release) {
        entered.countDown();
        try { if (!release.await(5, TimeUnit.SECONDS)) throw new IllegalStateException("Pregame test gate timed out"); }
        catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new IllegalStateException(failure); }
    }
    private static long named(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream()
                .filter(value -> text(value.getAsJsonObject(), "name").equals(name)).count();
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static Deck deck(String name, String basic, String commander) {
        var deck = new Deck(name); deck.getMain().add(StaticData.instance().getCommonCards().getCard(basic), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard(commander)); return deck;
    }
    private static List<JsonObject> main(List<MatchEndpoint> endpoints) throws Exception {
        var answered = new HashMap<Integer, String>(); long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(30);
        List<JsonObject> last = List.of();
        while (System.nanoTime() < deadline) {
            var states = endpoints.stream().map(endpoint -> object(endpoint.state())).toList();
            last = states;
            var host = states.get(0);
            if (text(host, "phaseKey").equals("MAIN1") && host.get("activePlayerId").equals(host.get("viewerId"))
                    && host.get("prompt").isJsonObject() && text(host.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority")
                    && host.getAsJsonArray("stack").isEmpty()) return states;
            for (int seat = 0; seat < states.size(); seat++) {
                var state = states.get(seat); assertNotEquals(text(state, "status"), "error", state.toString());
                if (!state.get("prompt").isJsonObject()) continue; var prompt = state.getAsJsonObject("prompt");
                if (text(prompt, "id").equals(answered.get(seat))) continue;
                Map<String, ?> command = Map.of("action", "ok");
                if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                    command = Map.of("action", "player", "playerId", host.get("viewerId").getAsInt());
                else if (prompt.has("choices") && !prompt.getAsJsonArray("choices").isEmpty())
                    command = Map.of("choices", prompt.getAsJsonArray("choices").asList().stream().limit(prompt.get("min").getAsInt())
                            .map(value -> value.getAsJsonObject().get("index").getAsInt()).toList());
                else if (prompt.has("optionalDecline") && !prompt.get("optionalDecline").isJsonNull()
                        && prompt.has("optionalPaymentSupport") && !prompt.get("optionalPaymentSupport").isJsonNull()
                        && text(prompt.getAsJsonObject("optionalPaymentSupport"), "availability").equals("Unpayable")
                        && prompt.get("cancelEnabled").getAsBoolean()) command = Map.of("action", "cancel");
                else if (!prompt.get("okEnabled").getAsBoolean()) continue;
                endpoints.get(seat).action(request(new Input(seat, state, prompt), command)); answered.put(seat, text(prompt, "id"));
            }
            Thread.sleep(5);
        }
        var evidence = ApiTestWorkspace.evidence("pregame-start-failure").resolve("last-frames.json");
        Files.writeString(evidence, JSON.toJson(last));
        throw new AssertionError("The real pregame fixture did not reach settled ordinary host priority: " + evidence);
    }
    private record LobbyClose(SyncLobby lobby) implements AutoCloseable { public void close() { lobby.close(); } }
    private static final class Fixture implements AutoCloseable {
        final SyncLobby host, guest;
        Fixture(String name, SyncLobby.PregameSetup setup) throws Exception {
            if (StaticData.instance() == null) EngineResources.load(RESOURCES);
            var profile = ApiTestWorkspace.profile(name);
            var cards = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
            var documents = new DeckDocumentSession(cards, profile.resolve("decks"), new DeckPlaytests(profile.resolve("decks")));
            documents.importDeck("Quiet red", "Deck\n99 Mountain\nCommander\n1 Rograkh, Son of Rohgahh", "Commander");
            documents.importDeck("Quiet white", "Deck\n99 Plains\nCommander\n1 Isamaru, Hound of Konda", "Commander");
            var catalog = new AiDeckCatalog(cards, documents);
            host = new SyncLobby(RESOURCES, profile.resolve("host"), true, catalog,
                    () -> CompletableFuture.completedFuture(Map.of()), () -> CompletableFuture.completedFuture(null),
                    () -> new EncounterProgressStore(profile.resolve("host")), setup);
            guest = new SyncLobby(RESOURCES, profile.resolve("guest"), false);
            var state = object(host.host("Commander", 4, false, "mixed"));
            String address = state.getAsJsonArray("addresses").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "label").equals("This computer")).findFirst().orElseThrow().get("url").getAsString();
            guest.join(address);
            host.selectDeck(deck("Host", "Island", "Toothy, Imaginary Friend"), "Commander");
            guest.selectDeck(deck("Friend", "Plains", "Isamaru, Hound of Konda"), "Commander");
            for (int seat : List.of(2, 3)) {
                String id = catalog.choices("Commander").stream().filter(row -> row.get("name").equals(seat == 2 ? "Quiet red" : "Quiet white"))
                        .findFirst().orElseThrow().get("id");
                host.setSeat(object(Map.of("tableId", text(state, "tableId"), "seat", seat, "type", "AI", "opponentId", id)));
            }
            host.ready(true); guest.ready(true);
        }
        List<MatchEndpoint> endpoints() { var endpoints = new ArrayList<MatchEndpoint>(List.of(host.match(), guest.match())); endpoints.addAll(host.automatedSeats()); return endpoints; }
        public void close() { try { guest.close(); } finally { host.close(); } }
    }
}
