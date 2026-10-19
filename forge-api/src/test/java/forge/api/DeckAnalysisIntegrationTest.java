package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.card.CardRarity;
import forge.item.PaperCard;
import forge.util.Localizer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;
import static org.testng.Assert.*;

/** Exercises the real desktop RPC and durable workshop/analysis boundary together. */
public class DeckAnalysisIntegrationTest {
    private static final Gson JSON = new Gson();
    @BeforeClass public void language() {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages");
    }
    private record Fixture(Path folder, CardCatalog catalog, DesktopEngine engine, String id, long revision, PaperCard cut, PaperCard add, PaperCard forest) implements AutoCloseable {
        @Override public void close() { engine.close(); }
    }
    private static Fixture fixture() throws Exception {
        var cut = DeckAnalysisTest.card("Divination", "2 U", "Sorcery", "Draw two cards.");
        var add = DeckAnalysisTest.card("Quick Study", "2 U", "Instant", "Draw two cards.");
        var forest = DeckAnalysisTest.card("Forest", "no cost", "Basic Land Forest", "{T}: Add {G}.");
        var alternate = new PaperCard(cut.getRules(), "ALT", CardRarity.Common);
        var catalog = new CardCatalog(List.of(cut, add, forest, alternate));
        Path folder = Files.createTempDirectory("analysis-workshop-integration-");
        var engine = new DesktopEngine(catalog, folder);
        var created = JSON.toJsonTree(engine.dispatch("new", object(Map.of("name", "Shared deck")))).getAsJsonObject();
        String id = created.get("id").getAsString();
        var edited = JSON.toJsonTree(engine.dispatch("edit", object(Map.of("deckId", id, "revision", 0, "edits", List.of(
                new DeckEditor.Edit("Main", CardCatalog.id(cut), 1), new DeckEditor.Edit("Main", CardCatalog.id(forest), 59)))))).getAsJsonObject();
        return new Fixture(folder, catalog, engine, id, edited.getAsJsonObject("deck").get("revision").getAsLong(), cut, add, forest);
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static JsonObject scope(Fixture f) { return object(Map.of("deckId", f.id(), "revision", f.revision())); }
    private static DeckAnalysisStore.View analysis(Fixture f) throws Exception { return (DeckAnalysisStore.View) f.engine().dispatch("deckAnalysis", scope(f)); }
    private static DeckWorkshopStore.View workshop(Fixture f) throws Exception { return (DeckWorkshopStore.View) f.engine().dispatch("workshop", scope(f)); }
    private static JsonObject write(Fixture f, DeckAnalysisStore.View current) {
        var request = scope(f); request.addProperty("metadataRevision", current.metadataRevision()); request.addProperty("fingerprint", current.report().fingerprint()); return request;
    }

    @Test public void planAndFavoritesHaveOneAuthoritativeMetadataRevisionInBothViews() throws Exception {
        try (var f = fixture()) {
            var patch = scope(f); patch.addProperty("plan", "My original plan");
            patch.add("roles", JSON.toJsonTree(List.of(new DeckWorkshopStore.Role(CardCatalog.id(f.cut()), List.of("Favorites"), true, "Archidekt"))));
            f.engine().dispatch("workshopMetadata", patch);
            var reviewed = analysis(f);
            assertEquals(reviewed.metadataRevision(), workshop(f).metadataRevision());
            assertEquals(reviewed.report().profile().plan(), "My original plan");
            assertTrue(reviewed.report().cards().stream().filter(card -> card.name().equals("Divination")).findFirst().orElseThrow().protectedCard());
            assertTrue(reviewed.report().recommendations().isEmpty());
            var profile = new DeckAnalysis.Profile("", "control", "One shared edited plan", "A backup", "balanced", 0, List.of(), List.of());
            var save = write(f, reviewed); save.add("profile", JSON.toJsonTree(profile));
            var updated = (DeckAnalysisStore.View) f.engine().dispatch("analysisProfile", save);
            assertEquals(workshop(f).plan(), profile.plan());
            assertTrue(workshop(f).roles().stream().noneMatch(DeckWorkshopStore.Role::pinned));
            assertEquals(workshop(f).insights().roleCounts().get("Draw").intValue(), 1, "Editing a favorite must preserve the imported card's estimated functional roles");
            assertTrue(workshop(f).roles().stream().filter(role -> role.cardId().equals(CardCatalog.id(f.cut()))).allMatch(role -> role.origin().equals("local")),
                    "Player favorite edits must become local overrides, so a source refresh cannot undo them");
            assertFalse(updated.report().recommendations().isEmpty());
            assertThrows(IllegalArgumentException.class, () -> f.engine().dispatch("analysisProfile", save));
            var next = scope(f); next.addProperty("plan", "Edited from Build again"); f.engine().dispatch("workshopMetadata", next);
            assertEquals(analysis(f).report().profile().plan(), "Edited from Build again");
            var stale = write(f, updated); stale.addProperty("decision", "consider"); stale.addProperty("recommendationId", updated.report().recommendations().get(0).id());
            assertThrows(IllegalArgumentException.class, () -> f.engine().dispatch("analysisReview", stale));
        }
    }

    @Test public void consideringAndDecisionCommitTogetherWithoutChangingPlayableVersionOrTestResults() throws Exception {
        try (var f = fixture()) {
            var before = workshop(f); var reviewed = analysis(f);
            assertEquals(reviewed.report().fingerprint(), before.versionId()); assertEquals(reviewed.report().versionId(), before.versionId());
            var request = write(f, reviewed); request.addProperty("recommendationId", reviewed.report().recommendations().get(0).id());
            var response = JSON.toJsonTree(f.engine().dispatch("analysisConsider", request)).getAsJsonObject();
            assertEquals(response.getAsJsonObject("analysis").get("metadataRevision"), response.getAsJsonObject("workshop").get("metadataRevision"));
            var after = workshop(f); var updated = analysis(f);
            assertEquals(after.versionId(), before.versionId()); assertEquals(after.versions().size(), before.versions().size());
            assertEquals(after.considering().get(0).card().name(), "Quick Study"); assertEquals(after.considering().get(0).quantity(), 1);
            assertEquals(updated.history().get(0).decision(), "consider"); assertEquals(updated.history().get(0).fingerprint(), before.versionId());
            assertTrue(((List<?>) f.engine().dispatch("workshopTests", object(Map.of("deckId", f.id())))).isEmpty(), "A recommendation decision must not manufacture a game test");
            var again = write(f, updated); again.addProperty("recommendationId", reviewed.report().recommendations().get(0).id());
            f.engine().dispatch("analysisConsider", again);
            assertEquals(workshop(f).considering().get(0).quantity(), 1, "Considering the same addition twice must not silently add extra copies");
            assertEquals(analysis(f).history().size(), 2);
            var snapshot = JSON.toJsonTree(f.engine().dispatch("snapshot", new JsonObject())).getAsJsonObject();
            assertEquals(snapshot.getAsJsonObject("deck").get("revision").getAsLong(), f.revision());
        }
    }

    @Test public void failedMetadataWriteCannotSaveOnlyTheOptionOrOnlyTheDecision() throws Exception {
        try (var f = fixture()) {
            var reviewed = analysis(f); var before = workshop(f);
            Path blocked = f.folder().resolve("workshop").resolve(f.id() + ".tmp"); Files.createDirectory(blocked);
            var request = write(f, reviewed); request.addProperty("recommendationId", reviewed.report().recommendations().get(0).id());
            assertThrows(java.io.IOException.class, () -> f.engine().dispatch("analysisConsider", request));
            assertTrue(analysis(f).history().isEmpty()); assertTrue(workshop(f).considering().isEmpty());
            assertEquals(workshop(f).metadataRevision(), before.metadataRevision()); assertEquals(workshop(f).versionId(), before.versionId());
            Files.delete(blocked);
        }
    }

    @Test public void legacyGoalsMigrateOnWritePreservingOriginalSidecarAndCanonicalFavorites() throws Exception {
        Path folder = Files.createTempDirectory("legacy-analysis-migration-");
        var cut = DeckAnalysisTest.card("Divination", "2 U", "Sorcery", "Draw two cards."); var catalog = new CardCatalog(List.of(cut));
        String id = UUID.randomUUID().toString(); var deck = DeckAnalysisTest.deck(DeckAnalysisTest.entry(cut, "Main", 1));
        var legacy = new DeckAnalysisStore(folder, catalog); var profile = DeckAnalysisTest.profile("control", "balanced", List.of("Divination"), List.of());
        legacy.profile(id, deck, "Constructed", 0, object(profile), null);
        legacy.review(id, deck, "Constructed", 1, DeckAnalysisStore.fingerprint(deck, "Constructed"), "", "observation", "An old manual note", null);
        Path original = folder.resolve("analysis").resolve(id + ".json"); String originalText = Files.readString(original);
        var workshop = new DeckWorkshopStore(folder, catalog); var shared = new DeckAnalysisStore(folder, catalog, workshop);
        var read = shared.view(id, deck, "Constructed", null); assertEquals(read.report().profile(), profile); assertEquals(read.history().size(), 1);
        assertFalse(Files.exists(folder.resolve("workshop").resolve(id + ".json")), "Read-only analysis must not migrate or create a new version");
        var saved = shared.review(id, deck, "Constructed", read.metadataRevision(), read.report().fingerprint(), "", "observation", "A migrated manual note", null);
        assertEquals(saved.history().size(), 2); assertEquals(Files.readString(original), originalText);
        var workspace = workshop.view(id, deck, "Constructed"); assertEquals(workspace.plan(), profile.plan());
        assertTrue(workspace.roles().stream().anyMatch(role -> role.cardId().equals(CardCatalog.id(cut)) && role.pinned()));
        workshop.metadata(id, deck, "Constructed", object(Map.of("plan", "", "roles", List.of(new DeckWorkshopStore.Role(CardCatalog.id(cut), List.of("Draw"), false, "local")))));
        var unpinned = new DeckAnalysisStore(folder, catalog, workshop).view(id, deck, "Constructed", null);
        assertEquals(unpinned.report().profile().plan(), ""); assertTrue(unpinned.report().profile().protectedCards().isEmpty());
    }

    @Test public void reopeningCannotBypassTheProfileContentGuardAndPendingIntentsBlockAnalysisWrites() throws Exception {
        try (var f = fixture()) {
            var reviewed = analysis(f); var stale = write(f, reviewed); stale.add("profile", JSON.toJsonTree(DeckAnalysis.Profile.defaults()));
            f.engine().dispatch("edit", object(Map.of("deckId", f.id(), "revision", f.revision(), "edits", List.of(new DeckEditor.Edit("Main", CardCatalog.id(f.forest()), 58)))));
            f.engine().dispatch("new", new JsonObject());
            f.engine().dispatch("open", object(Map.of("id", f.id())));
            stale.addProperty("revision", 0);
            assertThrows(IllegalArgumentException.class, () -> f.engine().dispatch("analysisProfile", stale));
            Path intent = f.folder().resolve("workshop").resolve(f.id() + ".journal"); Files.writeString(intent, "Preserved pending intent");
            assertThrows(IllegalStateException.class, () -> f.engine().dispatch("deckAnalysis", object(Map.of("deckId", f.id(), "revision", 0))));
            assertEquals(Files.readString(intent), "Preserved pending intent"); Files.delete(intent);
        }
    }

    @Test public void duplicationCopiesGoalsButStartsIndependentReviewHistoryAndFormatChangesStayReadable() throws Exception {
        try (var f = fixture()) {
            var reviewed = analysis(f); var request = write(f, reviewed); request.addProperty("decision", "dismiss"); request.addProperty("recommendationId", reviewed.report().recommendations().get(0).id());
            f.engine().dispatch("analysisReview", request);
            var copy = JSON.toJsonTree(f.engine().dispatch("duplicate", object(Map.of("deckId", f.id(), "revision", f.revision(), "name", "An independent copy")))).getAsJsonObject();
            String copyId = copy.get("id").getAsString();
            var copied = (DeckAnalysisStore.View) f.engine().dispatch("deckAnalysis", object(Map.of("deckId", copyId, "revision", 0)));
            assertTrue(copied.history().isEmpty());
            var format = JSON.toJsonTree(f.engine().dispatch("format", object(Map.of("deckId", copyId, "revision", 0, "format", "Commander")))).getAsJsonObject();
            var changed = (DeckAnalysisStore.View) f.engine().dispatch("deckAnalysis", object(Map.of("deckId", copyId, "revision", format.getAsJsonObject("deck").get("revision").getAsLong())));
            assertEquals(changed.report().format().name(), "Commander");
        }
    }

    @Test public void rulesVersionsAreSharedWhileCosmeticPrintingChangesPreserveReviewIdentity() throws Exception {
        var card = DeckAnalysisTest.card("Example", "2 U", "Sorcery", "Draw two cards."); var info = CardCatalog.describe(card);
        var original = DeckAnalysisTest.deck(new DeckEditor.Entry("Main", info, 1));
        var print = DeckAnalysisTest.deck(new DeckEditor.Entry("Main", CardCatalog.describe(new PaperCard(card.getRules(), "ALT", CardRarity.Common)), 1));
        assertEquals(DeckAnalysisStore.fingerprint(print, "Constructed"), DeckWorkshopStore.fingerprint(original, "Constructed"));
        var altered = JSON.toJsonTree(info).getAsJsonObject(); altered.addProperty("manaCost", "1 U");
        var changed = DeckAnalysisTest.deck(new DeckEditor.Entry("Main", JSON.fromJson(altered, CardCatalog.CardInfo.class), 1));
        assertNotEquals(DeckAnalysisStore.fingerprint(changed, "Constructed"), DeckAnalysisStore.fingerprint(original, "Constructed"));
        Path folder = Files.createTempDirectory("analysis-malformed-"); String id = UUID.randomUUID().toString();
        Files.createDirectories(folder.resolve("analysis")); Path corrupted = folder.resolve("analysis").resolve(id + ".json"); Files.writeString(corrupted, "{}");
        assertThrows(java.io.IOException.class, () -> new DeckAnalysisStore(folder, new CardCatalog(List.of(card))).view(id, original, "Constructed", null));
        assertEquals(Files.readString(corrupted), "{}");
    }

    @Test public void explicitWorkshopClearsAndUnpinsWinOverLegacyGoalsBeforeMigration() throws Exception {
        Path folder = Files.createTempDirectory("legacy-cleared-goals-");
        var cut = DeckAnalysisTest.card("Divination", "2 U", "Sorcery", "Draw two cards."); var catalog = new CardCatalog(List.of(cut));
        String id = UUID.randomUUID().toString(); var deck = DeckAnalysisTest.deck(DeckAnalysisTest.entry(cut, "Main", 1));
        var legacy = new DeckAnalysisStore(folder, catalog);
        legacy.profile(id, deck, "Constructed", 0, object(DeckAnalysisTest.profile("control", "balanced", List.of("Divination", "Future card annotation"), List.of())), null);
        Path original = folder.resolve("analysis").resolve(id + ".json"); String originalText = Files.readString(original);
        var workshop = new DeckWorkshopStore(folder, catalog);
        workshop.metadata(id, deck, "Constructed", object(Map.of("plan", "A workshop plan", "roles", List.of())));
        workshop.metadata(id, deck, "Constructed", object(Map.of("plan", "")));
        var shared = new DeckAnalysisStore(folder, catalog, workshop); var read = shared.view(id, deck, "Constructed", null);
        assertEquals(read.report().profile().plan(), "");
        assertFalse(read.report().profile().protectedCards().contains("Divination"));
        assertTrue(read.report().profile().protectedCards().contains("Future card annotation"));
        assertFalse(read.report().cards().get(0).protectedCard());
        shared.review(id, deck, "Constructed", read.metadataRevision(), read.report().fingerprint(), "", "observation", "Migrate without reviving old goals", null);
        var updated = workshop.view(id, deck, "Constructed"); assertEquals(updated.plan(), ""); assertTrue(updated.roles().stream().noneMatch(DeckWorkshopStore.Role::pinned));
        assertEquals(Files.readString(original), originalText);
    }
}
