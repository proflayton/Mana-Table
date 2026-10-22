package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckSection;
import forge.item.PaperCard;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.api.sync.SyncTransport;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;
import static org.testng.Assert.*;

public class DeckExperienceTest {
    @BeforeClass public void resources() {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
    }
    private static PaperCard card(String name) {
        var card = StaticData.instance().getCommonCards().getCard(name); assertNotNull(card, name); return card;
    }
    @Test public void bundledCommanderBansAreCardLinkedAndConsideringDoesNotEnterValidation() {
        var forest = card("Forest"); var leader = card("Rhys the Redeemed"); var lotus = card("Black Lotus");
        var catalog = new CardCatalog(List.of(forest, leader, lotus));
        new DeckAnalysisFormats(catalog).load(Path.of("../forge-gui/res"));
        var deck = new Deck("Bundled eligibility"); deck.getMain().add(forest, 98); deck.getMain().add(lotus);
        deck.getOrCreate(DeckSection.Commander).add(leader);
        var editor = new DeckEditor(catalog, deck); var result = editor.validate(DeckFormat.Commander);
        assertTrue(result.structureValid()); assertFalse(result.valid()); assertEquals(result.eligibility(), "failed");
        var issue = result.issues().stream().filter(i -> i.code().equals("bundled-eligibility")).findFirst().orElseThrow();
        assertEquals(issue.cardIds(), List.of(CardCatalog.id(lotus))); assertEquals(issue.section(), "Main");
        editor.apply(0, List.of(new DeckEditor.Edit("Main", CardCatalog.id(lotus), 0), new DeckEditor.Edit("Main", CardCatalog.id(forest), 99)));
        assertTrue(editor.validate(DeckFormat.Commander).valid());
        assertEquals(editor.validate(DeckFormat.Commander).eligibility(), "bundled-rules");
        // Considering is workshop metadata, not a playable Deck section.
        assertFalse(editor.toDeck().getMain().contains(lotus));
    }
    @Test public void bundledEligibilityIncludesCommandersAndSideboardsWhileUnknownStaysExplicit() {
        var forest = card("Forest"); var rhys = card("Rhys the Redeemed"); var banned = card("Black Lotus"); var rofellos = card("Rofellos, Llanowar Emissary");
        var catalog = new CardCatalog(List.of(forest, rhys, banned, rofellos));
        var rules = new DeckAnalysisFormats(catalog); rules.load(Path.of("../forge-gui/res"));
        var deck = new Deck("Every section"); deck.getMain().add(forest, 99);
        deck.getOrCreate(DeckSection.Commander).add(rofellos); deck.getOrCreate(DeckSection.Sideboard).add(banned);
        var result = new DeckEditor(catalog, deck).validate(DeckFormat.Commander);
        assertTrue(result.issues().stream().anyMatch(i -> i.code().equals("bundled-eligibility") && i.section().equals("Commander")));
        assertTrue(result.issues().stream().anyMatch(i -> i.code().equals("bundled-eligibility") && i.section().equals("Sideboard")));
        var constructed = new Deck("Generic constructed"); constructed.getMain().add(forest, 60);
        var unknown = new DeckEditor(catalog, constructed).validate(DeckFormat.Constructed);
        assertTrue(unknown.structureValid()); assertEquals(unknown.eligibility(), "unknown");
    }
    @Test public void latestRecommendationDecisionReopensAndDoesNotFollowChangedPlayableContent() throws Exception {
        var divination = DeckAnalysisTest.card("Divination", "2 U", "Sorcery", "Draw two cards.");
        var quick = DeckAnalysisTest.card("Quick Study", "2 U", "Instant", "Draw two cards.");
        var catalog = new CardCatalog(List.of(divination, quick)); var path = Files.createTempDirectory("deck-decisions-");
        var store = new DeckAnalysisStore(path, catalog); String id = UUID.randomUUID().toString();
        var deck = DeckAnalysisTest.deck(DeckAnalysisTest.entry(divination, "Main", 1));
        var current = store.view(id, deck, "Constructed", null); var recommendation = current.report().recommendations().get(0);
        for (String decision : List.of("keep", "dismiss", "reopen")) {
            current = store.review(id, deck, "Constructed", current.metadataRevision(), current.report().fingerprint(), recommendation.id(), decision, "A deliberate decision", null);
            assertEquals(current.decisions().size(), 1); assertEquals(current.decisions().get(0).decision(), decision);
        }
        assertEquals(current.history().size(), 3); assertEquals(deck.entries().size(), 1);
        var reopened = new DeckAnalysisStore(path, catalog).view(id, deck, "Constructed", null);
        assertEquals(reopened.decisions().get(0).decision(), "reopen");
        var changed = DeckAnalysisTest.deck(DeckAnalysisTest.entry(divination, "Main", 2));
        var next = store.view(id, changed, "Constructed", null);
        assertTrue(next.decisions().isEmpty()); assertEquals(next.history().size(), 3);
        assertThrows(IllegalArgumentException.class, () -> store.review(id, changed, "Constructed", next.metadataRevision(), reopened.report().fingerprint(), recommendation.id(), "dismiss", "", null));
    }
    @Test public void testedDeckComparisonIsReadOnlyScopedAndRetainsAnEarlyEndedList() throws Exception {
        var forest = CardCatalog.describe(card("Forest"));
        var saved = List.of(new DeckEditor.Entry("Main", forest, 60));
        var path = Files.createTempDirectory("tested-version-"); var store = new DeckPlaytests(path);
        var test = store.begin("deck-a", "played-version", "Played name", "Constructed", saved, "game", "First three land drops?", List.of("green"));
        store.finish(test.id(), "conceded", "Defeat");
        var file = path.resolve("workshop-tests/" + test.id() + ".json"); var bytes = Files.readAllBytes(file);
        var current = new DeckEditor.Snapshot("Changed name", 12, List.of(new DeckEditor.Entry("Main", forest, 58)), true, false);
        var view = store.inspect("deck-a", test.id(), current, "Limited");
        assertEquals(view.name(), "Played name"); assertEquals(view.format(), "Constructed"); assertEquals(view.currentFormat(), "Limited");
        assertEquals(view.entries().get(0).quantity(), 60); assertEquals(view.differences().get(0).saved(), 60); assertEquals(view.differences().get(0).current(), 58);
        assertEquals(store.get("deck-a", test.id()).status(), "conceded");
        assertEquals(Files.readAllBytes(file), bytes); assertEquals(current.revision(), 12L);
        assertThrows(IllegalArgumentException.class, () -> store.inspect("other-deck", test.id(), current, "Limited"));
    }
    @Test public void versionInspectionDoesNotRestoreOrWriteMetadata() throws Exception {
        var forest = card("Forest"); var catalog = new CardCatalog(List.of(forest));
        var path = Files.createTempDirectory("version-inspection-"); var store = new DeckWorkshopStore(path, catalog);
        var deck = new Deck("My deck"); deck.getMain().add(forest, 60); var editor = new DeckEditor(catalog, deck);
        String id = UUID.randomUUID().toString(); String version = store.ensureVersion(id, editor.snapshot(), "Constructed");
        editor.apply(0, List.of(new DeckEditor.Edit("Main", CardCatalog.id(forest), 59)));
        var metadata = path.resolve("workshop/" + id + ".json"); var bytes = Files.readAllBytes(metadata);
        var before = editor.snapshot(); var inspected = store.inspect(id, version, before, "Constructed");
        assertEquals(inspected.entries().get(0).quantity(), 60); assertEquals(inspected.differences().get(0).current(), 59);
        assertEquals(editor.snapshot(), before); assertEquals(Files.readAllBytes(metadata), bytes);
        assertThrows(IllegalArgumentException.class, () -> store.inspect(id, "unknown", before, "Constructed"));
    }

    @Test public void deckLibraryMutationsRefuseConnectedLobbyLocalSeatAndUnfinishedGame() throws Exception {
        var json = new Gson(); var path = Files.createTempDirectory("deck-library-active-scope-");
        var catalog = new CardCatalog(List.of(card("Forest")));
        var status = new java.util.concurrent.atomic.AtomicReference<>("playing");
        try (var engine = new DesktopEngine(catalog, path);
             var server = new SyncTransport.Server((seat, method, arguments) -> Map.of("status", status.get()), () -> 0)) {
            var resourceField = DesktopEngine.class.getDeclaredField("resources"); resourceField.setAccessible(true);
            resourceField.set(engine, Path.of("../forge-gui/res"));
            var removed = json.toJsonTree(engine.dispatch("new", json.toJsonTree(Map.of("name", "Removed", "format", "Constructed")).getAsJsonObject())).getAsJsonObject();
            var deleteRemoved = json.toJsonTree(Map.of("deckId", removed.get("id").getAsString(), "libraryToken", removed.get("libraryToken").getAsString())).getAsJsonObject();
            var receipt = json.toJsonTree(engine.dispatch("deleteDeck", deleteRemoved)).getAsJsonObject();
            var undo = json.toJsonTree(Map.of("deckId", receipt.get("deckId").getAsString(), "restoreToken", receipt.get("restoreToken").getAsString())).getAsJsonObject();
            var live = json.toJsonTree(engine.dispatch("new", json.toJsonTree(Map.of("name", "Live", "format", "Constructed")).getAsJsonObject())).getAsJsonObject();
            var deleteLive = json.toJsonTree(Map.of("deckId", live.get("id").getAsString(), "libraryToken", live.get("libraryToken").getAsString())).getAsJsonObject();
            engine.dispatch("multiplayerHost", json.toJsonTree(Map.of("format", "Constructed", "playerCount", 2, "transport", "local")).getAsJsonObject());
            assertThrows(IllegalStateException.class, () -> engine.dispatch("deleteDeck", deleteLive));
            assertThrows(IllegalStateException.class, () -> engine.dispatch("restoreDeck", undo));
            var guest = json.toJsonTree(engine.dispatch("multiplayerLocalJoin", new JsonObject())).getAsJsonObject();
            var privateCommand = deleteLive.deepCopy(); privateCommand.add("connectionId", guest.get("connectionId"));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("deleteDeck", privateCommand));
            engine.dispatch("multiplayerClose", new JsonObject());
            assertThrows(IllegalStateException.class, () -> engine.dispatch("deleteDeck", deleteLive));
            assertThrows(IllegalStateException.class, () -> engine.dispatch("restoreDeck", undo));
            engine.dispatch("multiplayerClose", guest);
            // Exercise the current-match status guard through its real seat endpoint;
            // no rules simulation is needed to establish a still-active game boundary.
            var matchField = DesktopEngine.class.getDeclaredField("match"); matchField.setAccessible(true);
            matchField.set(engine, new SeatConnection(server.local(0)));
            assertThrows(IllegalStateException.class, () -> engine.dispatch("deleteDeck", deleteLive));
            assertThrows(IllegalStateException.class, () -> engine.dispatch("restoreDeck", undo));
            assertTrue(Files.exists(path.resolve(live.get("id").getAsString() + ".json")));
            assertFalse(Files.exists(path.resolve(removed.get("id").getAsString() + ".json")));
            status.set("finished");
            engine.dispatch("restoreDeck", undo); engine.dispatch("deleteDeck", deleteLive);
            assertTrue(Files.exists(path.resolve(removed.get("id").getAsString() + ".json")));
            assertFalse(Files.exists(path.resolve(live.get("id").getAsString() + ".json")));
        }
    }
}
