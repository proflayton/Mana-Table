package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

/** Actual committed deck documents and host seat authority; no strategy or match simulation. */
public class AiDeckSelectionTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private CardCatalog cards;

    @BeforeClass public void loadCards() {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        cards = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
    }

    private record Library(Path path, DeckDocumentSession documents, AiDeckCatalog opponents) { }
    private Library library() throws Exception {
        Path path = Files.createTempDirectory("mana-ai-decks-");
        var documents = new DeckDocumentSession(cards, path, new DeckPlaytests(path));
        return new Library(path, documents, new AiDeckCatalog(cards, documents));
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static JsonObject imported(Library library, String name, String list, String format) {
        return object(library.documents().importDeck(name, list, format));
    }
    private static JsonObject partners(Library library) {
        return imported(library, "Custom Pir and Toothy", "Deck\n98 Island\nCommander\n1 Toothy, Imaginary Friend\n1 Pir, Imaginative Rascal", "Commander");
    }
    private static String choice(AiDeckCatalog catalog, String name) {
        return catalog.choices("Commander").stream().filter(row -> row.get("name").equals(name)).findFirst().orElseThrow().get("id");
    }
    private static JsonObject seat(JsonObject lobby, int index) { return lobby.getAsJsonArray("slots").get(index).getAsJsonObject(); }
    private static JsonObject assign(JsonObject lobby, int index, String id) {
        return object(Map.of("tableId", lobby.get("tableId").getAsString(), "seat", index, "type", "AI", "opponentId", id));
    }

    @Test public void customDualCommandersAndPresetsShareAValidatedCommittedCatalog() throws Exception {
        var library = library();
        var dual = partners(library);
        imported(library, "Wrong format", "Deck\n60 Island", "Constructed");
        imported(library, "Incomplete Commander", "Deck\n10 Forest\nCommander\n1 Rhys the Redeemed", "Commander");
        imported(library, "Invalid partners", "Deck\n98 Island\nCommander\n1 Toothy, Imaginary Friend\n1 Rhys the Redeemed", "Commander");
        var choices = library.opponents().choices("Commander");
        assertTrue(choices.stream().anyMatch(row -> row.get("id").startsWith("preset:")));
        assertEquals(choices.stream().filter(row -> row.get("id").startsWith("deck:")).count(), 1L);
        String id = choice(library.opponents(), "Custom Pir and Toothy");
        assertTrue(choices.stream().filter(row -> row.get("id").equals(id)).findFirst().orElseThrow().get("description").contains("Saved deck"));
        var deck = library.opponents().load(id, "Commander");
        assertEquals(deck.getMain().countAll(), 98);
        assertEquals(deck.getCommanders().stream().map(card -> card.getName()).sorted().toList(),
                List.of("Pir, Imaginative Rascal", "Toothy, Imaginary Friend"));
        assertThrows(IllegalArgumentException.class, () -> library.opponents().load(id, "Constructed"));
        assertEquals(library.opponents().load("deck:" + dual.get("id").getAsString(), "Commander").getCommanders().size(), 2,
                "Existing saved-deck simulation references remain supported");
        try (var engine = new DesktopEngine(cards, library.path())) {
            var setup = object(engine.dispatch("matchSetup", object(Map.of("deckId", dual.get("id").getAsString()))));
            assertTrue(setup.getAsJsonArray("opponents").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(row -> row.get("id").getAsString().equals(id)), "Solo preparation exports the same committed saved choice through the real desktop protocol");
            assertEquals(setup.getAsJsonObject("setup").getAsJsonArray("commanders").size(), 2);
            assertEquals(object(engine.dispatch("snapshot", new JsonObject())).get("id"), dual.get("id"),
                    "Enumerating opponent decks does not replace the human's active editor");
        }
    }

    @Test(timeOut = 45000) public void onlyTheHostAssignsItsCommittedSnapshotAndRefreshNeverReloadsIt() throws Exception {
        var library = library();
        var dual = partners(library);
        var privateDeck = imported(library, "Private unselected host deck", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed", "Commander");
        var openedDual = object(library.documents().open(dual.get("id").getAsString()));
        String id = choice(library.opponents(), "Custom Pir and Toothy");
        var loaded = library.opponents().load(id, "Commander");
        var host = new SyncLobby(RESOURCES, library.path(), false, library.opponents());
        SyncLobby guest = null;
        try {
            var lobby = object(host.host("Commander", 3, false, "local")); guest = host.localGuest();
            var selected = object(host.setSeat(assign(lobby, 2, id)));
            assertEquals(seat(selected, 2).get("deck").getAsString(), "Custom Pir and Toothy");
            assertTrue(seat(selected, 2).get("ready").getAsBoolean());
            assertGuestSelection(object(guest.state()), dual, privateDeck);
            SyncLobby visitor = guest;
            assertThrows(IllegalArgumentException.class, () -> visitor.setSeat(assign(lobby, 2, id)));
            assertThrows(IllegalArgumentException.class, () -> host.setSeat(assign(lobby, 1, id)));
            var wrongTable = assign(lobby, 2, id); wrongTable.addProperty("tableId", "retired-table");
            assertThrows(IllegalArgumentException.class, () -> host.setSeat(wrongTable));

            String savedId = dual.get("id").getAsString();
            long revision = openedDual.getAsJsonObject("deck").get("revision").getAsLong();
            library.documents().rename(new DeckDocumentSession.RequestScope(savedId, revision), "Changed after assignment");
            assertFalse(library.opponents().choices("Commander").stream().anyMatch(row -> row.get("id").equals(id)));
            assertThrows(IllegalArgumentException.class, () -> host.setSeat(assign(lobby, 2, id)));
            for (int refresh = 0; refresh < 2; refresh++) {
                assertEquals(seat(object(host.state()), 2), seat(selected, 2), "Refresh preserves the selected immutable deck, not the editor's new name");
                assertGuestSelection(object(visitor.state()), dual, privateDeck);
            }
            assertFalse(JSON.toJson(host.state()).contains("\"cards\""), "Lobby catalogs publish descriptors rather than saved deck contents");
            host.selectDeck(loaded, "Commander"); guest.selectDeck(loaded, "Commander"); host.ready(true); guest.ready(true);
            assertTrue(object(host.state()).get("canStart").getAsBoolean());
        } finally { if (guest != null) guest.close(); host.close(); }
    }

    private static void assertGuestSelection(JsonObject guest, JsonObject selected, JsonObject unselected) {
        assertEquals(seat(guest, 2).get("deck").getAsString(), "Custom Pir and Toothy");
        assertTrue(seat(guest, 2).get("ready").getAsBoolean());
        assertTrue(guest.getAsJsonArray("aiOpponents").isEmpty(), "Only the host receives its private saved-deck catalog");
        assertTrue(!seat(guest, 2).has("opponentId") || seat(guest, 2).get("opponentId").isJsonNull(),
                "A guest needs the assigned public deck name, not its host-library reference");
        String wire = guest.toString();
        assertFalse(wire.contains(selected.get("id").getAsString()));
        assertFalse(wire.contains(unselected.get("id").getAsString()));
        assertFalse(wire.contains("Private unselected host deck"), "Unselected host deck names never enter the guest lobby projection");
    }

    @Test public void removedChangedAndUnsavedDocumentsCannotBeAssigned() throws Exception {
        var library = library();
        var dual = partners(library);
        String id = choice(library.opponents(), "Custom Pir and Toothy");
        String savedId = dual.get("id").getAsString();
        var removed = library.documents().delete(savedId, dual.get("libraryToken").getAsString());
        assertFalse(library.opponents().choices("Commander").stream().anyMatch(row -> row.get("id").equals(id)));
        assertThrows(Exception.class, () -> library.opponents().load(id, "Commander"));
        var receipt = object(removed);
        library.documents().restore(savedId, receipt.get("restoreToken").getAsString());
        var reopened = object(library.documents().open(savedId));
        long revision = reopened.getAsJsonObject("deck").get("revision").getAsLong();
        Path blocker = library.path().resolve(savedId + ".tmp"); Files.createDirectory(blocker);
        try {
            var unsaved = object(library.documents().rename(new DeckDocumentSession.RequestScope(savedId, revision), "Unsaved AI deck"));
            assertTrue(unsaved.has("saveError"));
            assertFalse(library.opponents().choices("Commander").stream().anyMatch(row -> row.get("id").startsWith("deck:")));
            assertThrows(IllegalStateException.class, () -> library.opponents().load(id, "Commander"));
        } finally { Files.delete(blocker); }
    }
}
