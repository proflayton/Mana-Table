package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;
import java.util.Set;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.Collectors;
import static org.testng.Assert.*;

/** Pregame assignments are authoritative snapshots, distinct from Ready and private saved documents. */
public class LobbyDeckProjectionTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static JsonObject seat(JsonObject lobby, int index) { return lobby.getAsJsonArray("slots").get(index).getAsJsonObject(); }
    private static JsonObject preview(JsonObject lobby, int index) { return seat(lobby, index).getAsJsonObject("deckPreview"); }
    private static long revision(JsonObject lobby) { return lobby.get("revision").getAsLong(); }
    private static List<String> addressLabels(JsonObject lobby) {
        return lobby.getAsJsonArray("addresses").asList().stream().map(row -> row.getAsJsonObject().get("label").getAsString()).toList();
    }
    private static JsonObject address(JsonObject lobby, String label) {
        return lobby.getAsJsonArray("addresses").asList().stream().map(row -> row.getAsJsonObject())
                .filter(row -> row.get("label").getAsString().equals(label)).findFirst().orElseThrow();
    }

    @Test(timeOut = 45000) public void addressDiscoveryNeverBlocksLobbyPollsAndGuestsNeverDiscoverHostAddresses() {
        var lanCalls = new AtomicInteger(); var internetCalls = new AtomicInteger(); var guestCalls = new AtomicInteger();
        var lan = new CompletableFuture<Map<String, String>>(); var internet = new CompletableFuture<String>();
        var host = new SyncLobby(RESOURCES, ApiTestWorkspace.profile("lobby-address-poll"), false, null,
                () -> { lanCalls.incrementAndGet(); return lan; }, () -> { internetCalls.incrementAndGet(); return internet; });
        var guest = new SyncLobby(RESOURCES, ApiTestWorkspace.profile("lobby-address-guest"), false, null,
                () -> { guestCalls.incrementAndGet(); throw new AssertionError("Guests do not enumerate local host addresses"); },
                () -> { guestCalls.incrementAndGet(); throw new AssertionError("Guests do not discover an external host address"); });
        try {
            var pending = object(host.host("Commander", 2, false, "mixed"));
            assertTrue(pending.get("addressLookupPending").getAsBoolean());
            assertEquals(addressLabels(pending), List.of("This computer"));
            String loopback = address(pending, "This computer").get("url").getAsString();
            for (int poll = 0; poll < 20; poll++) assertEquals(addressLabels(object(host.state())), List.of("This computer"));
            var joined = object(guest.join(loopback));
            assertTrue(joined.getAsJsonArray("addresses").isEmpty());
            assertFalse(joined.get("addressLookupPending").getAsBoolean());
            assertEquals(guestCalls.get(), 0);

            var discovered = new LinkedHashMap<String, String>();
            discovered.put("VPN", "10.2.3.4"); discovered.put("Wi-Fi", "192.168.2.3");
            lan.complete(discovered); discovered.clear(); discovered.put("Changed after capture", "10.9.9.9");
            var local = object(host.state());
            assertEquals(addressLabels(local), List.of("VPN", "Wi-Fi", "This computer"));
            assertTrue(local.get("addressLookupPending").getAsBoolean(), "LAN completion does not hide the still-pending external lookup");
            internet.complete("203.0.113.9");
            var complete = object(host.state());
            assertFalse(complete.get("addressLookupPending").getAsBoolean());
            assertEquals(addressLabels(complete), List.of("VPN", "Wi-Fi", "This computer", "Internet"));
            assertTrue(address(complete, "Internet").get("preferred").getAsBoolean());
            String port = loopback.substring(loopback.lastIndexOf(':'));
            assertEquals(NetworkInvite.address(address(complete, "Internet").get("invite").getAsString()), "203.0.113.9" + port);
            assertEquals(NetworkInvite.address(address(complete, "VPN").get("invite").getAsString()), "10.2.3.4" + port);
            host.reconnect(); for (int poll = 0; poll < 20; poll++) host.state();
            assertEquals(lanCalls.get(), 1); assertEquals(internetCalls.get(), 1);
        } finally { guest.close(); host.close(); }
    }

    @Test(timeOut = 45000) public void failedAddressDiscoveryIsAnEndpointFallbackAndRehostingRetries() {
        for (String failure : List.of("throw", "null-future", "null-result", "failed-future")) {
            var calls = new AtomicInteger();
            var host = new SyncLobby(RESOURCES, ApiTestWorkspace.profile("lobby-address-" + failure), false, null, () -> {
                if (calls.incrementAndGet() > 1) return CompletableFuture.completedFuture(Map.of("New endpoint", "10.4.5.6"));
                return switch (failure) {
                    case "throw" -> throw new IllegalStateException("Discovery unavailable");
                    case "null-future" -> null;
                    case "null-result" -> CompletableFuture.completedFuture(null);
                    default -> CompletableFuture.failedFuture(new IllegalStateException("Discovery failed"));
                };
            }, () -> CompletableFuture.failedFuture(new IllegalStateException("External discovery unavailable")));
            try {
                var failed = object(host.host("Commander", 2, false, "mixed"));
                assertEquals(addressLabels(failed), List.of("This computer"));
                assertFalse(failed.get("addressLookupPending").getAsBoolean());
                host.state(); host.state(); assertEquals(calls.get(), 1, "Polling never retries discovery on the engine command pipe");
                var retried = object(host.host("Commander", 2, false, "mixed"));
                assertEquals(addressLabels(retried), List.of("New endpoint", "This computer"));
                assertEquals(calls.get(), 2, "An empty or failed prior endpoint never poisons a new host");
            } finally { host.close(); }
        }
    }

    @Test(timeOut = 45000) public void oldAddressDiscoveryCannotReplaceANewEndpointAndLocalTablesNeverStartDiscovery() {
        var lanCalls = new AtomicInteger(); var internetCalls = new AtomicInteger();
        var oldLan = new CompletableFuture<Map<String, String>>(); var newLan = new CompletableFuture<Map<String, String>>();
        var oldInternet = new CompletableFuture<String>(); var newInternet = new CompletableFuture<String>();
        var host = new SyncLobby(RESOURCES, ApiTestWorkspace.profile("lobby-address-lifecycle"), false, null,
                () -> lanCalls.incrementAndGet() == 1 ? oldLan : newLan,
                () -> internetCalls.incrementAndGet() == 1 ? oldInternet : newInternet);
        try {
            var first = object(host.host("Commander", 2, false, "mixed"));
            var second = object(host.host("Commander", 2, false, "mixed"));
            assertNotEquals(first.get("tableId"), second.get("tableId"));
            oldLan.complete(Map.of("Old endpoint", "10.0.0.1")); oldInternet.complete("203.0.113.1");
            var waiting = object(host.state());
            assertEquals(addressLabels(waiting), List.of("This computer")); assertTrue(waiting.get("addressLookupPending").getAsBoolean());
            newInternet.complete("203.0.113.2");
            var lanPending = object(host.state());
            assertTrue(lanPending.get("addressLookupPending").getAsBoolean(), "External completion does not hide pending LAN discovery");
            assertEquals(addressLabels(lanPending), List.of("This computer", "Internet"));
            newLan.complete(Map.of("Current endpoint", "10.0.0.2"));
            var current = object(host.state());
            assertFalse(current.get("addressLookupPending").getAsBoolean());
            assertEquals(addressLabels(current), List.of("Current endpoint", "This computer", "Internet"));
            String loopback = address(current, "This computer").get("url").getAsString();
            String port = loopback.substring(loopback.lastIndexOf(':'));
            assertEquals(address(current, "Current endpoint").get("url").getAsString(), "10.0.0.2" + port);
            assertEquals(NetworkInvite.address(current.get("internetInvite").getAsString()), "203.0.113.2" + port);
            assertTrue(object(host.close()).getAsJsonArray("addresses").isEmpty());
            var local = object(host.host("Commander", 2, false, "local"));
            assertEquals(addressLabels(local), List.of("This computer"));
            assertEquals(address(local, "This computer").get("url").getAsString(), "127.0.0.1:0");
            assertFalse(local.get("addressLookupPending").getAsBoolean());
            assertEquals(lanCalls.get(), 2); assertEquals(internetCalls.get(), 2);
        } finally { host.close(); }
    }
    private static Deck rhys(String name) {
        var deck = new Deck(name); deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed")); return deck;
    }
    private static Deck partners(String name) {
        var deck = new Deck(name); deck.getMain().add(StaticData.instance().getCommonCards().getCard("Island"), 98);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Pir, Imaginative Rascal"));
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Toothy, Imaginary Friend")); return deck;
    }
    private static void assertPublicFaces(JsonObject preview, Set<String> names) {
        assertFalse(preview.get("selectionId").getAsString().isBlank());
        assertEquals(preview.getAsJsonArray("commanders").asList().stream()
                .map(face -> face.getAsJsonObject().get("name").getAsString()).collect(Collectors.toSet()), names);
        for (var face : preview.getAsJsonArray("commanders")) {
            var card = face.getAsJsonObject();
            assertFalse(card.get("artName").getAsString().isBlank());
            assertFalse(card.get("edition").getAsString().isBlank());
            assertFalse(card.has("key")); assertFalse(card.has("visualId")); assertFalse(card.has("selectable"));
        }
        assertFalse(preview.has("cards")); assertFalse(preview.has("entries"));
        assertFalse(preview.has("deckId")); assertFalse(preview.has("libraryToken"));
    }

    @Test(timeOut = 45000) public void humanAndAiAssignmentsPublishTheSamePublicIdentityBeforeReadyAndSurviveRefresh() throws Exception {
        var host = new SyncLobby(RESOURCES, Files.createTempDirectory("mana-lobby-preview-")); SyncLobby guest = null;
        try {
            var hosted = object(host.host("Commander", 3, false, "local"));
            guest = host.localGuest(); String tableId = hosted.get("tableId").getAsString();
            assertTrue(revision(object(host.state())) > revision(hosted), "Joining changes the shared lobby revision");
            var source = partners("Pir and Toothy");
            var selected = object(host.selectDeck(source, "Commander", tableId));
            assertFalse(seat(selected, 0).get("ready").getAsBoolean());
            assertPublicFaces(preview(selected, 0), Set.of("Pir, Imaginative Rascal", "Toothy, Imaginary Friend"));
            assertEquals(preview(object(guest.state()), 0), preview(selected, 0));
            source.getMain().clear();
            var visitor = object(guest.selectDeck(rhys("Guest Rhys"), "Commander", tableId));
            assertEquals(preview(object(host.state()), 1), preview(visitor, 1));
            assertPublicFaces(preview(visitor, 1), Set.of("Rhys the Redeemed"));
            host.ready(true); guest.ready(true); // Later changes to the caller's source do not alter the assigned owned list.
            var ready = object(host.state()); long readyRevision = revision(ready);
            host.ready(true);
            assertEquals(revision(object(host.state())), readyRevision, "Repeated Ready and reads do not create revisions");

            var renamed = object(host.selectDeck(partners("Renamed partners"), "Commander", tableId));
            assertTrue(revision(renamed) > readyRevision);
            assertFalse(seat(renamed, 0).get("ready").getAsBoolean());
            assertTrue(seat(renamed, 1).get("ready").getAsBoolean(), "Another player's consent is independent");
            assertEquals(seat(renamed, 0).get("deck").getAsString(), "Renamed partners");
            assertNotEquals(preview(renamed, 0).get("selectionId"), preview(selected, 0).get("selectionId"));
            assertEquals(preview(object(guest.state()), 0), preview(renamed, 0));

            String opponent = renamed.getAsJsonArray("aiOpponents").get(0).getAsJsonObject().get("id").getAsString();
            var withAi = object(host.setSeat(object(Map.of("tableId", tableId, "seat", 2, "type", "AI", "opponentId", opponent))));
            assertFalse(preview(withAi, 2).getAsJsonArray("commanders").isEmpty());
            assertTrue(seat(withAi, 2).get("ready").getAsBoolean());
            for (int poll = 0; poll < 2; poll++) {
                var remote = object(guest.state());
                for (int index = 0; index < 3; index++) assertEquals(preview(remote, index), preview(withAi, index));
                assertEquals(revision(remote), revision(withAi));
            }
            host.chat(object(Map.of("tableId", tableId, "kind", "chat", "text", "hello")));
            assertEquals(revision(object(host.state())), revision(withAi), "Chat has its own conversation revision");
        } finally { if (guest != null) guest.close(); host.close(); }
    }

    @Test(timeOut = 45000) public void draftSelectionReplacesOldConsentWithoutDisclosingPrivateDiagnosticsAndClearIsExplicit() throws Exception {
        var host = new SyncLobby(RESOURCES, Files.createTempDirectory("mana-lobby-draft-")); SyncLobby guest = null;
        try {
            var hosted = object(host.host("Commander", 2, false, "local")); guest = host.localGuest();
            String tableId = hosted.get("tableId").getAsString();
            host.selectDeck(rhys("Original legal deck"), "Commander", tableId); host.ready(true);
            var draft = rhys("New draft"); draft.getMain().remove(StaticData.instance().getCommonCards().getCard("Forest"), 1);
            draft.getMain().add(StaticData.instance().getCommonCards().getCard("Counterspell"));
            var selected = object(host.selectDeck(draft, "Commander", tableId));
            assertFalse(seat(selected, 0).get("ready").getAsBoolean());
            assertNotNull(preview(selected, 0).get("problem"));
            var remote = object(guest.state());
            assertEquals(preview(remote, 0), preview(selected, 0));
            assertFalse(remote.toString().contains("Counterspell"), "Private legality diagnostics stay with the selecting player");
            assertFalse(remote.toString().contains("Forest"), "The public preview contains no main-deck list");
            assertThrows(IllegalArgumentException.class, () -> host.ready(true));
            assertEquals(revision(object(host.state())), revision(selected), "Rejected Ready has no mutation");
            assertFalse(object(host.state()).get("canStart").getAsBoolean());
            var cleared = object(host.clearDeck(tableId));
            assertFalse(seat(cleared, 0).has("deckPreview"));
            assertFalse(seat(cleared, 0).has("deck"));
            assertTrue(revision(cleared) > revision(selected));
            assertEquals(revision(object(host.clearDeck(tableId))), revision(cleared));
            assertFalse(seat(object(guest.state()), 0).has("deckPreview"));
        } finally { if (guest != null) guest.close(); host.close(); }
    }

    @Test(timeOut = 45000) public void savedSelectionReceiptRejectsChangedVersionsAndForeignTablesBeforeReplacingTheSeat() throws Exception {
        try (var engine = new DesktopEngine(CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values()),
                Files.createTempDirectory("mana-lobby-selection-scope-"))) {
            // The production pipe entry point supplies this after resource load;
            // direct-dispatch fixtures must establish the same ready lifecycle.
            var resourceField = DesktopEngine.class.getDeclaredField("resources"); resourceField.setAccessible(true);
            resourceField.set(engine, RESOURCES);
            var saved = object(engine.dispatch("import", object(Map.of("name", "First saved name", "text",
                    "Deck\n98 Island\nCommander\n1 Pir, Imaginative Rascal\n1 Toothy, Imaginary Friend", "format", "Commander"))));
            var hosted = object(engine.dispatch("multiplayerHost", object(Map.of("format", "Commander", "playerCount", 2, "transport", "local"))));
            String tableId = hosted.get("tableId").getAsString();
            var selection = object(Map.of("tableId", tableId, "deckId", saved.get("id").getAsString(),
                    "libraryToken", saved.get("libraryToken").getAsString()));
            var initial = object(engine.dispatch("multiplayerSelectDeck", selection));
            assertPublicFaces(preview(initial, 0), Set.of("Pir, Imaginative Rascal", "Toothy, Imaginary Friend"));
            var opened = object(engine.dispatch("open", object(Map.of("id", saved.get("id").getAsString()))));
            var renamed = object(engine.dispatch("rename", object(Map.of("deckId", saved.get("id").getAsString(),
                    "revision", opened.getAsJsonObject("deck").get("revision").getAsLong(), "name", "Acknowledged rename"))));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("multiplayerSelectDeck", selection));
            var wrongTable = selection.deepCopy(); wrongTable.addProperty("tableId", "retired-table");
            wrongTable.addProperty("libraryToken", renamed.get("libraryToken").getAsString());
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("multiplayerSelectDeck", wrongTable));
            assertEquals(preview(object(engine.dispatch("multiplayerState", new JsonObject())), 0), preview(initial, 0));
            assertEquals(revision(object(engine.dispatch("multiplayerState", new JsonObject()))), revision(initial));
            selection.addProperty("libraryToken", renamed.get("libraryToken").getAsString());
            var current = object(engine.dispatch("multiplayerSelectDeck", selection));
            assertEquals(preview(current, 0).get("name").getAsString(), "Acknowledged rename");
            assertFalse(seat(current, 0).toString().contains(saved.get("id").getAsString()), "Private saved IDs are not public seat identities");
            assertFalse(seat(current, 0).toString().contains(renamed.get("libraryToken").getAsString()), "Private version tokens stay out of previews");
            var cleared = object(engine.dispatch("multiplayerSelectDeck", object(Map.of("tableId", tableId, "deckId", ""))));
            assertFalse(seat(cleared, 0).has("deckPreview"));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("multiplayerReady", object(Map.of("ready", true))));
        }
    }
}
