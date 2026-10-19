package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import java.nio.file.Files;
import java.util.List;
import java.util.UUID;
import org.testng.annotations.Test;
import static org.testng.Assert.*;

public class DeckAnalysisStoreTest {
    @Test public void profilesAndVersionedObservationsSurviveReopeningWithoutChangingPlayableDecks() throws Exception {
        var folder = Files.createTempDirectory("analysis-store-");
        var forest = DeckAnalysisTest.card("Forest", "no cost", "Basic Land Forest", "{T}: Add {G}.");
        var catalog = new CardCatalog(List.of(forest));
        String id = UUID.randomUUID().toString();
        var deck = DeckAnalysisTest.deck(DeckAnalysisTest.entry(forest, "Main", 24));
        var store = new DeckAnalysisStore(folder, catalog);
        var original = store.view(id, deck, "Constructed", null);
        assertFalse(Files.exists(folder.resolve("analysis")), "Reading an analysis must not create metadata or change a deck");
        var profile = DeckAnalysisTest.profile("control", "balanced", List.of("Forest"), List.of());
        var saved = store.profile(id, deck, "Constructed", 0, new Gson().toJsonTree(profile).getAsJsonObject(), null);
        assertEquals(saved.metadataRevision(), 1L);
        assertThrows(IllegalArgumentException.class, () -> store.profile(id, deck, "Constructed", 0, new Gson().toJsonTree(profile).getAsJsonObject(), null));
        var noted = store.review(id, deck, "Constructed", 1, original.report().fingerprint(), "", "observation", "Kept two lands; missed turn-three land drop on the play.", null);
        assertEquals(noted.history().size(), 1);
        var reopened = new DeckAnalysisStore(folder, catalog).view(id, deck, "Constructed", null);
        assertEquals(reopened.report().profile(), profile);
        assertEquals(reopened.history(), noted.history());
        var changed = DeckAnalysisTest.deck(DeckAnalysisTest.entry(forest, "Main", 25));
        var next = store.view(id, changed, "Constructed", null);
        assertNotEquals(next.report().fingerprint(), original.report().fingerprint());
        assertEquals(next.history().get(0).fingerprint(), original.report().fingerprint());
        assertThrows(IllegalArgumentException.class, () -> store.review(id, changed, "Constructed", 2, original.report().fingerprint(), "", "observation", "stale", null));
        assertEquals(deck.entries().get(0).quantity(), 24);
    }
    @Test public void corruptNotesArePreservedAndUnknownDecisionsAreRejected() throws Exception {
        var folder = Files.createTempDirectory("analysis-corrupt-"); var catalog = new CardCatalog(List.of());
        String id = UUID.randomUUID().toString(); var deck = DeckAnalysisTest.deck();
        var store = new DeckAnalysisStore(folder, catalog); var view = store.view(id, deck, "Constructed", null);
        assertThrows(IllegalArgumentException.class, () -> store.review(id, deck, "Constructed", 0, view.report().fingerprint(), "invented", "consider", "", null));
        assertThrows(IllegalArgumentException.class, () -> store.review(id, deck, "Constructed", 0, view.report().fingerprint(), "", "observation", "", null));
        Files.createDirectories(folder.resolve("analysis"));
        var file = folder.resolve("analysis").resolve(id + ".json"); Files.writeString(file, "{broken");
        assertThrows(java.io.IOException.class, () -> store.view(id, deck, "Constructed", null));
        assertEquals(Files.readString(file), "{broken");
    }
    @Test public void protocolBindsAnalysisWritesToTheActiveDeckAndBothRevisions() throws Exception {
        var folder = Files.createTempDirectory("analysis-api-");
        try (var engine = new DesktopEngine(new CardCatalog(List.of()), folder)) {
            var json = new Gson();
            var created = json.toJsonTree(engine.dispatch("new", new JsonObject())).getAsJsonObject();
            String id = created.get("id").getAsString(); long revision = created.getAsJsonObject("deck").get("revision").getAsLong();
            var request = new JsonObject(); request.addProperty("deckId", id); request.addProperty("revision", revision);
            var view = (DeckAnalysisStore.View) engine.dispatch("deckAnalysis", request);
            var write = request.deepCopy(); write.addProperty("metadataRevision", view.metadataRevision());
            write.add("profile", json.toJsonTree(DeckAnalysisTest.profile("aggro", "fast", List.of(), List.of())));
            engine.dispatch("analysisProfile", write);
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("analysisProfile", write));
            engine.dispatch("new", new JsonObject());
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("deckAnalysis", request));
        }
    }
}
