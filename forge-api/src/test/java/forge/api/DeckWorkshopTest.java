package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.Deck;
import forge.item.PaperCard;
import org.testng.annotations.Test;
import org.testng.annotations.BeforeClass;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.testng.Assert.*;

/** Real durable storage and offline source fixtures, including concurrent edits and interrupted writes. */
public class DeckWorkshopTest {
    private static final Gson JSON = new Gson();
    @BeforeClass public void language() { forge.util.Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages"); }
    private static PaperCard card(String name, String edition, String mana, String type, String oracle) {
        return new PaperCard(new CardRules.Reader().readCard(List.of("Name:" + name, "ManaCost:" + mana,
                "Types:" + type, "PT:1/1", "Oracle:" + oracle)), edition, CardRarity.Common);
    }
    private record Fixture(Path path, CardCatalog catalog, DeckWorkshopStore store, String id, DeckEditor editor,
                           CardCatalog.CardInfo a, CardCatalog.CardInfo b, CardCatalog.CardInfo c, CardCatalog.CardInfo forest) { }
    private static Fixture fixture() throws Exception {
        Path path = Files.createTempDirectory("mana-workshop-");
        var a = card("First", "TST", "1 G", "Sorcery", "Draw a card.");
        var b = card("Second", "TST", "2 G", "Sorcery", "Search your library for a basic land card, put it onto the battlefield tapped, then shuffle.");
        var c = card("Candidate", "TST", "3 G", "Creature Elf", "Vigilance");
        var forest = card("Forest", "TST", "no cost", "Basic Land Forest", "{T}: Add {G}.");
        var leader = card("Leader", "TST", "G W", "Legendary Creature Elf", "Vigilance");
        var catalog = new CardCatalog(List.of(a, b, c, forest, leader, card("First", "ALT", "1 G", "Sorcery", "Draw a card.")));
        return new Fixture(path, catalog, new DeckWorkshopStore(path, catalog), UUID.randomUUID().toString(),
                new DeckEditor(catalog, new Deck("My plan")), CardCatalog.describe(a), CardCatalog.describe(b), CardCatalog.describe(c), CardCatalog.describe(forest));
    }
    private static JsonObject patch(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static JsonObject command(DesktopEngine engine, String method, Object value) throws Exception {
        return patch(engine.dispatch(method, patch(value)));
    }
    private static JsonObject create(DesktopEngine engine, String name) throws Exception {
        return command(engine, "new", Map.of("name", name, "format", "Constructed"));
    }
    private static JsonObject remove(DesktopEngine engine, JsonObject deck) throws Exception {
        return command(engine, "deleteDeck", Map.of("deckId", deck.get("id").getAsString(), "libraryToken", deck.get("libraryToken").getAsString()));
    }
    private static JsonObject restore(DesktopEngine engine, JsonObject receipt) throws Exception {
        return command(engine, "restoreDeck", Map.of("deckId", receipt.get("deckId").getAsString(), "restoreToken", receipt.get("restoreToken").getAsString()));
    }
    private static int count(DeckEditor editor, String name, String section) {
        return editor.snapshot().entries().stream().filter(row -> row.card().name().equals(name) && row.section().equals(section)).mapToInt(DeckEditor.Entry::quantity).sum();
    }
    private static ArchidektSource.Remote remote(Fixture f, int first, int second, int candidate, String updated) {
        String json = "{\"id\":123,\"name\":\"Linked elves\",\"deckFormat\":3,\"updatedAt\":\"" + updated + "\","
                + "\"categories\":[{\"name\":\"Maybeboard\",\"includedInDeck\":false}],\"cards\":["
                + row("First", first, "[\"Draw\",\"Favorites\"]") + "," + row("Second", second, "[\"Ramp\"]") + ","
                + row("Forest", 4, "[\"Land\",\"Mana\"]") + "," + row("Leader", 1, "[\"Commander\"]") + ","
                + row("Candidate", candidate, "[\"Maybeboard\",\"Tokens\"]") + "]}";
        return ArchidektSource.parse("123", JsonParser.parseString(json).getAsJsonObject(), f.catalog());
    }
    private static String row(String name, int quantity, String tags) {
        return "{\"quantity\":" + quantity + ",\"categories\":" + tags + ",\"notes\":\"Keep this printing\",\"modifier\":\"Normal\","
                + "\"card\":{\"displayName\":\"" + name + "\",\"collectorNumber\":\"999\",\"edition\":{\"editioncode\":\"TST\"},\"oracleCard\":{\"name\":\"" + name + "\"}}}";
    }
    private static DeckWorkshopStore.Applied imported(Fixture f) throws Exception {
        var preview = f.store().stage(remote(f, 1, 1, 1, "A"), null, null, null);
        return f.store().apply(preview.token(), null, null, "Commander", "remote");
    }

    @Test public void durableVersionsIgnoreRenamePrintingAndNotesAndCanRestore() throws Exception {
        var f = fixture();
        f.editor().apply(0, List.of(new DeckEditor.Edit("Main", f.a().id(), 1), new DeckEditor.Edit("Main", f.forest().id(), 9)));
        var original = f.store().view(f.id(), f.editor().snapshot(), "Constructed");
        var updated = f.store().metadata(f.id(), f.editor().snapshot(), "Constructed", patch(Map.of("metadataRevision", 0,
                "plan", "Elves that draw cards", "question", "Can I make my fourth land drop?",
                "roles", List.of(new DeckWorkshopStore.Role(f.a().id(), List.of("draw", "Draw", "ramp", "Ramp", "Payoff", "payoff"), true, "local")),
                "considering", List.of(new DeckWorkshopStore.ConsideringEdit(f.c().id(), 1, List.of("Tokens"))))));
        assertEquals(updated.versionId(), original.versionId());
        assertEquals(updated.versions().size(), 1);
        assertEquals(updated.considering().get(0).card().name(), "Candidate");
        assertEquals(updated.insights().roleCounts().get("Draw").intValue(), 1);
        assertEquals(updated.insights().roleCounts().get("Ramp").intValue(), 1);
        assertEquals(updated.insights().roleCounts().get("Payoff").intValue(), 1);
        assertEquals(updated.roles().stream().filter(role -> role.cardId().equals(f.a().id())).findFirst().orElseThrow().roles(), List.of("Draw", "Ramp", "Payoff"));
        assertEquals(count(f.editor(), "Candidate", "Main"), 0);
        var roundTrip = f.store().metadata(f.id(), f.editor().snapshot(), "Constructed", patch(Map.of("roles", updated.roles())));
        assertTrue(roundTrip.roles().stream().anyMatch(role -> role.cardId().equals(f.forest().id()) && role.origin().equals("estimated")),
                "Saving an untouched estimated role must not mislabel it as a player override");
        var withCustom = new java.util.ArrayList<>(roundTrip.roles().stream().filter(role -> !role.cardId().equals(f.forest().id())).toList());
        withCustom.add(new DeckWorkshopStore.Role(f.forest().id(), List.of("Lands", "PAYOFF"), false, "local"));
        var aggregated = f.store().metadata(f.id(), f.editor().snapshot(), "Constructed", patch(Map.of("roles", withCustom)));
        assertEquals(aggregated.insights().roleCounts().get("Payoff").intValue(), 10);
        assertEquals(aggregated.insights().roleCounts().keySet().stream().filter(key -> key.equalsIgnoreCase("payoff")).count(), 1L);
        assertThrows(IllegalArgumentException.class, () -> f.store().metadata(f.id(), f.editor().snapshot(), "Constructed", patch(Map.of("metadataRevision", 0, "plan", "stale"))));
        f.editor().rename(f.editor().snapshot().revision(), "A new name");
        assertEquals(f.store().view(f.id(), f.editor().snapshot(), "Constructed").versionId(), original.versionId());
        var alternate = f.catalog().browse(new CardCatalog.Query("First", null, null, 0, 20), "", "name", false).cards().stream().filter(c -> c.edition().equals("ALT")).findFirst().orElseThrow();
        f.editor().apply(f.editor().snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.a().id(), 0), new DeckEditor.Edit("Main", alternate.id(), 1)));
        assertEquals(f.store().view(f.id(), f.editor().snapshot(), "Constructed").versionId(), original.versionId());
        f.editor().apply(f.editor().snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.b().id(), 1)));
        assertNotEquals(f.store().view(f.id(), f.editor().snapshot(), "Constructed").versionId(), original.versionId());
        f.store().restore(f.id(), f.editor(), "Constructed", original.versionId());
        var reopened = new DeckWorkshopStore(f.path(), f.catalog()).view(f.id(), f.editor().snapshot(), "Constructed");
        assertEquals(reopened.versionId(), original.versionId());
        assertEquals(reopened.versions().size(), 2);
        assertEquals(reopened.plan(), "Elves that draw cards");
        assertTrue(reopened.roles().stream().filter(r -> r.cardId().equals(f.a().id())).findFirst().orElseThrow().pinned());
    }

    @Test public void sourceImportPreservesExcludedCardsCategoriesAndOriginalMetadata() throws Exception {
        var f = fixture(); var source = remote(f, 1, 1, 1, "A");
        assertTrue(source.problems().isEmpty());
        assertEquals(source.cards().size(), 5, "Overlapping tags must not duplicate physical cards");
        assertTrue(source.cards().stream().filter(row -> row.card().name().equals("Candidate")).noneMatch(ArchidektSource.Row::included));
        var result = imported(f);
        var view = f.store().view(result.deckId(), result.editor().snapshot(), result.format());
        assertEquals(count(result.editor(), "Candidate", "Main"), 0);
        assertEquals(count(result.editor(), "Leader", "Commander"), 1);
        assertEquals(view.considering().get(0).quantity(), 1);
        assertEquals(view.insights().roleCounts().get("Lands").intValue(), 4);
        assertEquals(view.insights().roleCounts().get("Draw").intValue(), 1);
        var lowercaseTags = source.original().deepCopy();
        lowercaseTags.getAsJsonArray("cards").get(0).getAsJsonObject().getAsJsonArray("categories").add("draw");
        var lowercasePreview = f.store().stage(ArchidektSource.parse("123", lowercaseTags, f.catalog()), null, null, null);
        var lowercaseImport = f.store().apply(lowercasePreview.token(), null, null, "Commander", "remote");
        assertEquals(f.store().view(lowercaseImport.deckId(), lowercaseImport.editor().snapshot(), "Commander").insights().roleCounts().get("Draw").intValue(), 1,
                "Estimated and imported categories differing only in case must count the physical card once");
        assertEquals(view.insights().coloredSources().get("G").intValue(), 4);
        assertTrue(view.roles().stream().anyMatch(role -> role.roles().contains("Favorites")));
        String saved = Files.readString(f.path().resolve("workshop").resolve(result.deckId() + ".json"));
        assertTrue(saved.contains("Keep this printing")); assertTrue(saved.contains("999"));
        var unknown = source.original().deepCopy();
        unknown.getAsJsonArray("cards").get(4).getAsJsonObject().getAsJsonObject("card").getAsJsonObject("oracleCard").addProperty("name", "Unmapped card");
        var bad = f.store().stage(ArchidektSource.parse("123", unknown, f.catalog()), null, null, null);
        assertFalse(bad.problems().isEmpty(), "Even excluded unknown cards must be reported");
        assertThrows(IllegalArgumentException.class, () -> f.store().apply(bad.token(), null, null, "Commander", "remote"));
    }

    @Test public void threeWayRefreshMergesIndependentEditsAndDoesNotManufactureVersions() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        editor.apply(editor.snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.a().id(), 2)));
        var changed = f.store().stage(remote(f, 1, 2, 1, "B"), id, editor.snapshot(), "Commander");
        assertTrue(changed.hasChanges()); assertTrue(changed.changes().stream().noneMatch(DeckWorkshopStore.Change::conflict));
        f.store().apply(changed.token(), id, editor, "Commander", "merge");
        assertEquals(count(editor, "First", "Main"), 2); assertEquals(count(editor, "Second", "Main"), 2);
        var before = f.store().view(id, editor.snapshot(), "Commander"); long revision = editor.snapshot().revision();
        var same = f.store().stage(remote(f, 1, 2, 1, "different-service-timestamp"), id, editor.snapshot(), "Commander");
        assertFalse(same.hasChanges());
        f.store().apply(same.token(), id, editor, "Commander", "merge");
        assertEquals(editor.snapshot().revision(), revision);
        var after = f.store().view(id, editor.snapshot(), "Commander");
        assertEquals(after.versionId(), before.versionId()); assertEquals(after.versions().size(), before.versions().size());
        assertEquals(after.source().updatedAt(), "different-service-timestamp");
    }

    @Test public void logicalDiffRetainsDistinctPrintingsWhenTheSourceOrLocalSideWins() throws Exception {
        var f = fixture();
        var source = remote(f, 1, 1, 1, "A").original().deepCopy();
        var another = source.getAsJsonArray("cards").get(0).getAsJsonObject().deepCopy();
        another.getAsJsonObject("card").getAsJsonObject("edition").addProperty("editioncode", "ALT");
        source.getAsJsonArray("cards").add(another);
        var remote = ArchidektSource.parse("123", source, f.catalog());
        var preview = f.store().stage(remote, null, null, null);
        var applied = f.store().apply(preview.token(), null, null, "Commander", "remote");
        assertEquals(count(applied.editor(), "First", "Main"), 2);
        assertEquals(applied.editor().snapshot().entries().stream().filter(row -> row.card().name().equals("First")).count(), 2L);
        var again = f.store().stage(remote, applied.deckId(), applied.editor().snapshot(), "Commander");
        assertTrue(again.changes().isEmpty());
        f.store().apply(again.token(), applied.deckId(), applied.editor(), "Commander", "merge");
        assertEquals(applied.editor().snapshot().entries().stream().filter(row -> row.card().name().equals("First")).count(), 2L);
    }

    @Test public void conflictsAndStaleLocalOrMetadataEditsCannotBeOverwritten() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        editor.apply(editor.snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.a().id(), 2)));
        var conflict = f.store().stage(remote(f, 3, 1, 1, "B"), id, editor.snapshot(), "Commander");
        assertTrue(conflict.changes().stream().anyMatch(DeckWorkshopStore.Change::conflict));
        long revision = editor.snapshot().revision();
        assertThrows(IllegalArgumentException.class, () -> f.store().apply(conflict.token(), id, editor, "Commander", "merge"));
        assertEquals(editor.snapshot().revision(), revision); assertEquals(count(editor, "First", "Main"), 2);
        f.store().metadata(id, editor.snapshot(), "Commander", patch(Map.of("question", "Do I need more ramp?")));
        assertThrows(IllegalArgumentException.class, () -> f.store().apply(conflict.token(), id, editor, "Commander", "remote"));
        var again = f.store().stage(remote(f, 3, 1, 1, "B"), id, editor.snapshot(), "Commander");
        editor.apply(editor.snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.b().id(), 2)));
        assertThrows(IllegalArgumentException.class, () -> f.store().apply(again.token(), id, editor, "Commander", "remote"));
        var local = f.store().stage(remote(f, 3, 1, 1, "B"), id, editor.snapshot(), "Commander");
        f.store().apply(local.token(), id, editor, "Commander", "local");
        assertEquals(count(editor, "First", "Main"), 2);
        assertFalse(f.store().stage(remote(f, 3, 1, 1, "B"), id, editor.snapshot(), "Commander").hasChanges());
    }

    @Test public void atomicConsideringSwapSurvivesRestartAndBadCutDoesNotChangeAnything() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var before = f.store().view(id, editor.snapshot(), "Commander");
        assertThrows(IllegalArgumentException.class, () -> f.store().move(id, editor, "Commander", before.metadataRevision(), f.c().id(), "Main", f.forest().id() + "missing"));
        assertEquals(f.store().view(id, editor.snapshot(), "Commander").versionId(), before.versionId());
        f.store().move(id, editor, "Commander", before.metadataRevision(), f.c().id(), "Main", f.a().id());
        assertEquals(count(editor, "First", "Main"), 0); assertEquals(count(editor, "Candidate", "Main"), 1);
        var after = new DeckWorkshopStore(f.path(), f.catalog()).view(id, editor.snapshot(), "Commander");
        assertEquals(after.considering().size(), 1); assertEquals(after.considering().get(0).card().name(), "First");
        assertEquals(after.versions().size(), before.versions().size() + 1);
        assertThrows(IllegalArgumentException.class, () -> f.store().move(id, editor, "Commander", before.metadataRevision(), f.b().id(), "Considering", null));
        var stored = JsonParser.parseString(Files.readString(f.path().resolve(id + ".json"))).getAsJsonObject();
        assertEquals(stored.getAsJsonArray("cards").size(), editor.snapshot().entries().size());
    }

    @Test public void durableIntentRecoversBothHalvesAfterInterruptedCommit() throws Exception {
        var f = fixture(); var result = imported(f); String id = result.deckId();
        var intent = new JsonObject();
        intent.add("deck", JsonParser.parseString(Files.readString(f.path().resolve(id + ".json"))));
        intent.add("workshop", JsonParser.parseString(Files.readString(f.path().resolve("workshop").resolve(id + ".json"))));
        Files.writeString(f.path().resolve("workshop").resolve(id + ".journal"), JSON.toJson(intent));
        Files.writeString(f.path().resolve(id + ".json"), "{}");
        Files.writeString(f.path().resolve("workshop").resolve(id + ".json"), "{}");
        var recovered = new DeckWorkshopStore(f.path(), f.catalog());
        assertNotNull(recovered.view(id, result.editor().snapshot(), "Commander").source());
        assertTrue(JsonParser.parseString(Files.readString(f.path().resolve(id + ".json"))).getAsJsonObject().has("cards"));
        assertFalse(Files.exists(f.path().resolve("workshop").resolve(id + ".journal")));
    }

    @Test public void removedDeckSurvivesRestartAndUndoRetainsContentsAndWorkspaceEvidence() throws Exception {
        var f = fixture(); var imported = imported(f); String id = imported.deckId();
        var before = f.store().view(id, imported.editor().snapshot(), "Commander");
        f.store().metadata(id, imported.editor().snapshot(), "Commander", patch(Map.of("plan", "Tokens and cards", "question", "Enough ramp?")));
        f.store().move(id, imported.editor(), "Commander", before.metadataRevision() + 1, f.c().id(), "Main", f.a().id());
        var analysis = new DeckAnalysisStore(f.path(), f.catalog());
        var report = analysis.view(id, imported.editor().snapshot(), "Commander", null);
        analysis.review(id, imported.editor().snapshot(), "Commander", report.metadataRevision(), report.report().fingerprint(), "", "observation", "Try another land", null);
        var playtests = new DeckPlaytests(f.path());
        var played = playtests.begin(id, f.store().view(id, imported.editor().snapshot(), "Commander").versionId(), "Linked elves", "Commander",
                imported.editor().snapshot().entries(), "game", "Enough ramp?", List.of("green"));
        playtests.finish(played.id(), "conceded", "Defeat");
        var deckFile = f.path().resolve(id + ".json"); var deckBytes = Files.readAllBytes(deckFile);
        var workshopFile = f.path().resolve("workshop/" + id + ".json"); var workshopBytes = Files.readAllBytes(workshopFile);
        var analysisFile = f.path().resolve("analysis/" + id + ".json"); var analysisBytes = Files.readAllBytes(analysisFile);
        var testFile = f.path().resolve("workshop-tests/" + played.id() + ".json"); var testBytes = Files.readAllBytes(testFile);
        JsonObject receipt;
        try (var engine = new DesktopEngine(f.catalog(), f.path()); var stale = new DesktopEngine(f.catalog(), f.path())) {
            var opened = command(engine, "open", Map.of("id", id));
            command(stale, "open", Map.of("id", id));
            receipt = remove(engine, opened);
            assertEquals(receipt.get("deckId").getAsString(), id); assertEquals(receipt.get("name").getAsString(), "Linked elves");
            assertFalse(Files.exists(deckFile));
            assertTrue(command(engine, "list", Map.of()).getAsJsonArray("decks").isEmpty());
            assertThrows(IllegalStateException.class, () -> engine.dispatch("save", patch(Map.of("deckId", id, "revision", 0))));
            assertThrows(IllegalStateException.class, () -> stale.dispatch("edit", patch(Map.of("deckId", id, "revision", 0,
                    "edits", List.of(new DeckEditor.Edit("Main", f.forest().id(), 99))))));
            assertThrows(IllegalStateException.class, () -> f.store().metadata(id, imported.editor().snapshot(), "Commander", patch(Map.of("plan", "Must not resurrect"))));
            assertFalse(Files.exists(deckFile));
        }
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            var removed = JSON.toJsonTree(engine.dispatch("deletedDecks", new JsonObject())).getAsJsonArray();
            assertEquals(removed.size(), 1); assertEquals(removed.get(0), receipt);
            var restored = restore(engine, receipt);
            assertEquals(restored.get("id").getAsString(), id);
            assertEquals(restored.get("libraryToken").getAsString(), DeckLibraryTrash.token(deckBytes));
            assertThrows(IllegalStateException.class, () -> engine.dispatch("snapshot", new JsonObject()));
            assertEquals(Files.readAllBytes(deckFile), deckBytes); assertEquals(Files.readAllBytes(workshopFile), workshopBytes);
            assertEquals(Files.readAllBytes(analysisFile), analysisBytes); assertEquals(Files.readAllBytes(testFile), testBytes);
            assertEquals(JSON.toJsonTree(engine.dispatch("deletedDecks", new JsonObject())).getAsJsonArray().size(), 0);
            command(engine, "open", Map.of("id", id));
            assertEquals(f.store().view(id, imported.editor().snapshot(), "Commander").versions().size(), 2);
            assertEquals(f.store().view(id, imported.editor().snapshot(), "Commander").plan(), "Tokens and cards");
            assertEquals(new DeckAnalysisStore(f.path(), f.catalog()).view(id, imported.editor().snapshot(), "Commander", null).history().get(0).note(), "Try another land");
            assertEquals(new DeckPlaytests(f.path()).get(id, played.id()).status(), "conceded");
        }
    }

    @Test public void deletingAnotherTilePreservesAnUnsavedOpenDocumentAndCurrentDeleteRefusesIt() throws Exception {
        var f = fixture();
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            var other = create(engine, "Other saved deck"); var current = create(engine, "Current document");
            String id = current.get("id").getAsString();
            Path blocker = f.path().resolve(id + ".tmp"); Files.createDirectory(blocker);
            var unsaved = command(engine, "rename", Map.of("deckId", id, "revision", 0, "name", "Unsaved new name"));
            assertTrue(unsaved.has("saveError"));
            assertThrows(IllegalStateException.class, () -> remove(engine, unsaved));
            assertTrue(Files.exists(f.path().resolve(id + ".json")));
            var receipt = remove(engine, other);
            assertEquals(command(engine, "snapshot", Map.of()), unsaved);
            restore(engine, receipt);
            assertEquals(command(engine, "snapshot", Map.of()), unsaved, "Undo must not switch editors or discard an unsaved document");
            Files.delete(blocker);
            var saved = command(engine, "save", Map.of("deckId", id, "revision", 1));
            assertFalse(saved.has("saveError"));
            assertEquals(saved.getAsJsonObject("deck").get("name").getAsString(), "Unsaved new name");
            assertTrue(Files.exists(f.path().resolve(other.get("id").getAsString() + ".json")));
        }
    }

    @Test public void repeatedExplicitSetupPreservesEditorHistoryAndItsPracticeAttachment() throws Exception {
        var f = fixture();
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            var created = create(engine, "Selected document");
            String id = created.get("id").getAsString();
            var edited = command(engine, "edit", Map.of("deckId", id, "revision", 0,
                    "edits", List.of(new DeckEditor.Edit("Main", f.forest().id(), 8))));
            long revision = edited.getAsJsonObject("deck").get("revision").getAsLong();
            var hand = command(engine, "workshopPractice", Map.of("deckId", id, "revision", revision, "action", "new"));
            var legacy = command(engine, "practice", Map.of("action", "shuffle"));
            for (int attempt = 0; attempt < 2; attempt++) {
                var setup = command(engine, "matchSetup", Map.of("deckId", id));
                assertEquals(setup.get("deckId").getAsString(), id);
                assertEquals(setup.get("revision").getAsLong(), revision);
                assertEquals(command(engine, "snapshot", Map.of()), edited,
                        "Preparing the current document must not reopen it, reset undo or manufacture a new revision");
            }
            var drawn = command(engine, "workshopPractice", Map.of("deckId", id, "revision", revision, "action", "draw"));
            assertEquals(drawn.get("testId"), hand.get("testId"));
            assertEquals(drawn.getAsJsonArray("hand").size(), 8);
            assertEquals(command(engine, "practice", Map.of("action", "draw")).getAsJsonArray("hand").size(),
                    legacy.getAsJsonArray("hand").size() + 1);

            var reopened = command(engine, "open", Map.of("id", id));
            long reopenedRevision = reopened.getAsJsonObject("deck").get("revision").getAsLong();
            assertFalse(reopened.getAsJsonObject("deck").get("canUndo").getAsBoolean());
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("workshopPractice",
                    patch(Map.of("deckId", id, "revision", reopenedRevision, "action", "draw"))));
            assertEquals(command(engine, "practice", Map.of("action", "draw")).getAsJsonArray("hand").size(), 0,
                    "An explicit reopen still revokes both practice attachments");
        }
    }

    @Test public void explicitSetupSwitchesDocumentsButWritesNeverSelectTheirTarget() throws Exception {
        var f = fixture();
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            String first = create(engine, "First document").get("id").getAsString();
            String second = create(engine, "Second document").get("id").getAsString();
            var setup = command(engine, "matchSetup", Map.of("deckId", first));
            assertEquals(setup.get("deckId").getAsString(), first);
            assertEquals(setup.get("name").getAsString(), "First document");
            assertEquals(setup.get("revision").getAsLong(), 0);
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("rename",
                    patch(Map.of("deckId", second, "revision", 0, "name", "Wrong document"))));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("format",
                    patch(Map.of("deckId", second, "revision", 0, "format", "Commander"))));
            assertEquals(command(engine, "snapshot", Map.of()).get("id").getAsString(), first);
            var next = command(engine, "matchSetup", Map.of("deckId", second));
            assertEquals(next.get("deckId").getAsString(), second);
            assertEquals(next.get("name").getAsString(), "Second document");
            assertEquals(command(engine, "matchSetup", Map.of()), next,
                    "Legacy setup without a deck ID still reads the selected document");
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("edit", patch(Map.of(
                    "deckId", first, "revision", 0, "edits", List.of(new DeckEditor.Edit("Main", f.forest().id(), 1))))));
            assertEquals(command(engine, "snapshot", Map.of()).get("id").getAsString(), second);
        }
    }

    @Test public void setupSwitchFailurePreservesTheUnsavedDocumentAndRejectsMissingTargets() throws Exception {
        var f = fixture();
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            String other = create(engine, "Other document").get("id").getAsString();
            String current = create(engine, "Current document").get("id").getAsString();
            Path blocker = f.path().resolve(current + ".tmp"); Files.createDirectory(blocker);
            var unsaved = command(engine, "rename", Map.of("deckId", current, "revision", 0, "name", "Unsaved name"));
            assertTrue(unsaved.has("saveError"));
            assertThrows(IllegalStateException.class, () -> engine.dispatch("matchSetup", patch(Map.of("deckId", other))));
            var same = command(engine, "matchSetup", Map.of("deckId", current));
            assertEquals(same.get("saveError"), unsaved.get("saveError"));
            assertEquals(command(engine, "snapshot", Map.of()), unsaved);
            Files.delete(blocker);
            var saved = command(engine, "save", Map.of("deckId", current, "revision", 1));
            assertThrows(java.io.IOException.class, () -> engine.dispatch("matchSetup",
                    patch(Map.of("deckId", UUID.randomUUID().toString()))));
            assertEquals(command(engine, "snapshot", Map.of()), saved,
                    "Loading a missing target must not partly replace the current document");
            Files.writeString(f.path().resolve("workshop/" + other + ".json"), "{broken");
            assertThrows(java.io.IOException.class, () -> engine.dispatch("matchSetup", patch(Map.of("deckId", other))));
            assertEquals(command(engine, "snapshot", Map.of()), saved,
                    "Failed workshop loading must not commit a switch to the target document");
        }
    }

    @Test public void preparedMatchContentsAreDetachedAndTheirScopeExpiresAcrossDocumentTransitions() throws Exception {
        var f = fixture();
        var documents = new DeckDocumentSession(f.catalog(), f.path(), new DeckPlaytests(f.path()));
        String id = patch(documents.create("Prepared document", "Constructed")).get("id").getAsString();
        var initial = new DeckDocumentSession.RequestScope(id, 0L);
        documents.edit(initial, List.of(new DeckEditor.Edit("Main", f.forest().id(), 8)));
        var prepared = documents.prepareSetup(id, "");
        documents.requireCurrent(prepared);
        prepared.deck().getMain().clear();
        var state = patch(documents.state());
        assertEquals(state.getAsJsonObject("deck").getAsJsonArray("entries").get(0).getAsJsonObject().get("quantity").getAsInt(), 8,
                "A consumer's detached match copy must not mutate the active editor");

        documents.rename(new DeckDocumentSession.RequestScope(id, prepared.scope().revision()), "New name");
        assertThrows(IllegalArgumentException.class, () -> documents.requireCurrent(prepared));
        var beforeOpen = documents.prepareSetup(id, "");
        documents.open(id);
        assertThrows(IllegalArgumentException.class, () -> documents.requireCurrent(beforeOpen));
        var beforeSwitch = documents.prepareSetup(id, "");
        documents.create("Other document", "Constructed");
        assertThrows(IllegalArgumentException.class, () -> documents.requireCurrent(beforeSwitch));
        var returned = documents.prepareSetup(id, "");
        assertEquals(returned.scope(), beforeSwitch.scope(), "A reopened document can have the same public ID and numeric revision");
        assertThrows("Returning to the same public scope must not revive a capture from an earlier document lifetime",
                IllegalArgumentException.class, () -> documents.requireCurrent(beforeSwitch));
        documents.requireCurrent(returned);
        Path journal = f.path().resolve("workshop/" + id + ".journal");
        Files.writeString(journal, "An interrupted write must revoke start admission");
        assertThrows(IllegalStateException.class, () -> documents.requireCurrent(returned));
        Files.delete(journal);
        Path blocker = f.path().resolve(id + ".tmp"); Files.createDirectory(blocker);
        assertTrue(patch(documents.save(new DeckDocumentSession.RequestScope(id, returned.scope().revision()))).has("saveError"));
        assertThrows(IllegalStateException.class, () -> documents.requireCurrent(returned));
        Files.delete(blocker);
    }

    @Test public void removedTilesRemainOrderedAndOldDeleteAndUndoTokensCannotAffectNewerCopies() throws Exception {
        var f = fixture(); JsonObject first, second, firstReceipt, secondReceipt;
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            first = create(engine, "First deck"); second = create(engine, "Second deck");
            firstReceipt = remove(engine, first); secondReceipt = remove(engine, second);
            assertTrue(secondReceipt.get("deletedAt").getAsLong() > firstReceipt.get("deletedAt").getAsLong());
        }
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            var receipts = JSON.toJsonTree(engine.dispatch("deletedDecks", new JsonObject())).getAsJsonArray();
            assertEquals(receipts.get(0), secondReceipt); assertEquals(receipts.get(1), firstReceipt);
            restore(engine, secondReceipt); restore(engine, firstReceipt);
            assertThrows(IllegalArgumentException.class, () -> restore(engine, firstReceipt));
            String id = first.get("id").getAsString(); command(engine, "open", Map.of("id", id));
            var renamed = command(engine, "rename", Map.of("deckId", id, "revision", 0, "name", "Newer name"));
            var bytes = Files.readAllBytes(f.path().resolve(id + ".json"));
            assertThrows(IllegalArgumentException.class, () -> remove(engine, first));
            assertEquals(Files.readAllBytes(f.path().resolve(id + ".json")), bytes);
            var newerReceipt = remove(engine, renamed);
            assertThrows(IllegalArgumentException.class, () -> restore(engine, firstReceipt));
            assertFalse(Files.exists(f.path().resolve(id + ".json")));
            restore(engine, newerReceipt);
            assertEquals(Files.readAllBytes(f.path().resolve(id + ".json")), bytes);
        }
    }

    @Test public void invalidMissingAndCollidingDeckLibraryRequestsNeverOverwriteFiles() throws Exception {
        var f = fixture();
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            var deck = create(engine, "Safe contents"); String id = deck.get("id").getAsString();
            for (String bad : List.of("../outside", "", id.toUpperCase(java.util.Locale.ROOT), "1-1-1-1-1")) {
                assertThrows(IllegalArgumentException.class, () -> engine.dispatch("deleteDeck", patch(Map.of("deckId", bad, "libraryToken", ""))));
                assertThrows(IllegalArgumentException.class, () -> engine.dispatch("restoreDeck", patch(Map.of("deckId", bad, "restoreToken", UUID.randomUUID().toString()))));
            }
            assertThrows(java.io.IOException.class, () -> engine.dispatch("deleteDeck", patch(Map.of("deckId", UUID.randomUUID().toString(), "libraryToken", "missing"))));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("deleteDeck", patch(Map.of("deckId", id, "libraryToken", "wrong"))));
            var receipt = remove(engine, deck);
            assertThrows(IllegalStateException.class, () -> remove(engine, deck));
            var path = f.path().resolve(id + ".json"); Files.writeString(path, "A newer file must survive Undo");
            assertThrows(IllegalStateException.class, () -> restore(engine, receipt));
            assertEquals(Files.readString(path), "A newer file must survive Undo");
            assertEquals(JSON.toJsonTree(engine.dispatch("deletedDecks", new JsonObject())).getAsJsonArray().size(), 1);
            Files.delete(path); restore(engine, receipt);
            assertEquals(JsonParser.parseString(Files.readString(path)).getAsJsonObject().get("name").getAsString(), "Safe contents");
        }
    }

    @Test public void removalRefusesPendingIntentAndRecoveryCannotResurrectAnExplicitlyRemovedDeck() throws Exception {
        var f = fixture(); var imported = imported(f); String id = imported.deckId();
        Path deck = f.path().resolve(id + ".json"), journal = f.path().resolve("workshop/" + id + ".journal");
        var intent = new JsonObject(); intent.add("deck", JsonParser.parseString(Files.readString(deck)));
        intent.add("workshop", JsonParser.parseString(Files.readString(f.path().resolve("workshop/" + id + ".json"))));
        JsonObject receipt;
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            var opened = command(engine, "open", Map.of("id", id)); Files.writeString(journal, JSON.toJson(intent));
            assertThrows(IllegalStateException.class, () -> remove(engine, opened)); assertTrue(Files.exists(deck));
            Files.delete(journal); receipt = remove(engine, opened);
        }
        // An already-running stale writer may leave an intent after deletion. Keep it,
        // but recovery must wait for the player's explicit Undo before restoring files.
        intent.getAsJsonObject("deck").addProperty("name", "Recovered pending change");
        Files.writeString(journal, JSON.toJson(intent));
        try (var engine = new DesktopEngine(f.catalog(), f.path())) {
            assertFalse(Files.exists(deck)); assertTrue(Files.exists(journal));
            assertTrue(command(engine, "list", Map.of()).getAsJsonArray("decks").isEmpty());
            var restored = restore(engine, receipt);
            assertEquals(restored.get("name").getAsString(), "Recovered pending change");
            assertFalse(Files.exists(journal)); assertTrue(Files.exists(deck));
            assertNotNull(new DeckWorkshopStore(f.path(), f.catalog()).view(id, imported.editor().snapshot(), "Commander").source());
        }
    }

    @Test public void duplicateCopiesTheWholeWorkspaceWithIndependentHistoryAndSourceBaseline() throws Exception {
        var f = fixture(); var original = imported(f); String id = original.deckId();
        f.store().metadata(id, original.editor().snapshot(), "Commander", patch(Map.of("plan", "My token theme", "question", "Need more draw?",
                "roles", List.of(new DeckWorkshopStore.Role(f.a().id(), List.of("Pet card"), true, "local")))));
        original.editor().apply(original.editor().snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.b().id(), 2)));
        var before = f.store().view(id, original.editor().snapshot(), "Commander");
        assertEquals(before.versions().size(), 2);
        var copy = new DeckEditor(f.catalog(), new Deck(original.editor().toDeck(), "Independent copy"));
        String copyId = UUID.randomUUID().toString();
        f.store().duplicate(id, copyId, copy.snapshot(), "Commander");
        var copied = f.store().view(copyId, copy.snapshot(), "Commander");
        assertEquals(copied.versions().size(), 1); assertEquals(copied.versions().get(0).label(), "v1");
        assertEquals(copied.plan(), before.plan()); assertEquals(copied.question(), before.question());
        assertEquals(copied.roles(), before.roles()); assertEquals(copied.considering(), before.considering());
        assertEquals(copied.source(), before.source());
        var preview = f.store().stage(remote(f, 1, 3, 1, "B"), copyId, copy.snapshot(), "Commander");
        assertTrue(preview.changes().stream().anyMatch(DeckWorkshopStore.Change::conflict), "Copied baseline must still detect overlapping remote edits");
        f.store().metadata(copyId, copy.snapshot(), "Commander", patch(Map.of("plan", "Separate plan")));
        assertEquals(f.store().view(id, original.editor().snapshot(), "Commander").plan(), "My token theme");
    }

    @Test public void undoRedoFollowsTheMoveBoundaryAndPreservesUnrelatedWorkspaceEdits() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var before = f.store().view(id, editor.snapshot(), "Commander");
        f.store().move(id, editor, "Commander", before.metadataRevision(), f.c().id(), "Main", f.a().id());
        var swapped = f.store().view(id, editor.snapshot(), "Commander");
        var considering = new java.util.ArrayList<DeckWorkshopStore.ConsideringEdit>();
        swapped.considering().forEach(row -> considering.add(new DeckWorkshopStore.ConsideringEdit(row.cardId(), row.quantity(), row.categories())));
        considering.add(new DeckWorkshopStore.ConsideringEdit(f.b().id(), 3, List.of("Manual idea")));
        f.store().metadata(id, editor.snapshot(), "Commander", patch(Map.of("plan", "Preserve my plan", "considering", considering)));
        editor.apply(editor.snapshot().revision(), List.of(new DeckEditor.Edit("Main", f.forest().id(), 5)));
        f.store().history(id, editor, "Commander", "undo", editor.snapshot().revision());
        assertEquals(count(editor, "Forest", "Main"), 4);
        assertEquals(count(editor, "Candidate", "Main"), 1, "Undoing an ordinary edit must not undo the earlier workspace swap");
        assertTrue(f.store().view(id, editor.snapshot(), "Commander").considering().stream().anyMatch(row -> row.cardId().equals(f.a().id())));
        f.store().history(id, editor, "Commander", "undo", editor.snapshot().revision());
        assertEquals(count(editor, "First", "Main"), 1); assertEquals(count(editor, "Candidate", "Main"), 0);
        var undone = f.store().view(id, editor.snapshot(), "Commander");
        assertEquals(undone.plan(), "Preserve my plan");
        assertTrue(undone.considering().stream().anyMatch(row -> row.cardId().equals(f.c().id()) && row.quantity() == 1));
        assertTrue(undone.considering().stream().anyMatch(row -> row.cardId().equals(f.b().id()) && row.quantity() == 3));
        assertFalse(undone.considering().stream().anyMatch(row -> row.cardId().equals(f.a().id())), "The cut must return to Main exactly once");
        f.store().history(id, editor, "Commander", "redo", editor.snapshot().revision());
        f.store().history(id, editor, "Commander", "redo", editor.snapshot().revision());
        assertEquals(count(editor, "Forest", "Main"), 5); assertEquals(count(editor, "Candidate", "Main"), 1);
        var redone = f.store().view(id, editor.snapshot(), "Commander");
        assertTrue(redone.considering().stream().anyMatch(row -> row.cardId().equals(f.a().id()) && row.quantity() == 1));
        assertTrue(redone.considering().stream().anyMatch(row -> row.cardId().equals(f.b().id()) && row.quantity() == 3));
        assertFalse(redone.considering().stream().anyMatch(row -> row.cardId().equals(f.c().id())));
    }

    @Test public void incompatibleManualConsideringRemovalRejectsUndoBeforeChangingTheDeck() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var before = f.store().view(id, editor.snapshot(), "Commander");
        f.store().move(id, editor, "Commander", before.metadataRevision(), f.c().id(), "Main", f.a().id());
        f.store().metadata(id, editor.snapshot(), "Commander", patch(Map.of("considering", List.of())));
        long revision = editor.snapshot().revision();
        assertThrows(IllegalArgumentException.class, () -> f.store().history(id, editor, "Commander", "undo", revision));
        assertEquals(editor.snapshot().revision(), revision);
        assertEquals(count(editor, "First", "Main"), 0); assertEquals(count(editor, "Candidate", "Main"), 1);
    }

    @Test public void failedUndoIntentWriteLeavesBothDeckAndConsideringAtTheCurrentBoundary() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var before = f.store().view(id, editor.snapshot(), "Commander");
        f.store().move(id, editor, "Commander", before.metadataRevision(), f.c().id(), "Main", f.a().id());
        var swapped = f.store().view(id, editor.snapshot(), "Commander");
        long revision = editor.snapshot().revision();
        Path blocked = f.path().resolve("workshop").resolve(id + ".journal.tmp");
        Files.createDirectory(blocked);
        assertThrows(java.io.IOException.class, () -> f.store().history(id, editor, "Commander", "undo", revision));
        assertEquals(editor.snapshot().revision(), revision);
        assertEquals(count(editor, "First", "Main"), 0); assertEquals(count(editor, "Candidate", "Main"), 1);
        var unchanged = f.store().view(id, editor.snapshot(), "Commander");
        assertEquals(unchanged.considering(), swapped.considering()); assertEquals(unchanged.metadataRevision(), swapped.metadataRevision());
        Files.delete(blocked);
        f.store().history(id, editor, "Commander", "undo", revision);
        assertEquals(count(editor, "First", "Main"), 1); assertEquals(count(editor, "Candidate", "Main"), 0);
    }

    @Test public void formatChangesInvalidateSourcePreviewsAndFollowUndoRedo() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var preview = f.store().stage(remote(f, 1, 2, 1, "B"), id, editor.snapshot(), "Commander");
        long revision = editor.snapshot().revision();
        f.store().format(id, editor, "Commander", "Constructed");
        assertTrue(editor.snapshot().revision() > revision);
        assertThrows(IllegalArgumentException.class, () -> f.store().apply(preview.token(), id, editor, "Constructed", "remote"));
        assertEquals(f.store().history(id, editor, "Constructed", "undo", editor.snapshot().revision()).format(), "Commander");
        assertEquals(f.store().history(id, editor, "Commander", "redo", editor.snapshot().revision()).format(), "Constructed");
    }

    @Test public void durableIntentBlocksFurtherWritesUntilRecoveryCompletes() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var before = f.store().view(id, editor.snapshot(), "Commander"); long revision = editor.snapshot().revision();
        Path deckPath = f.path().resolve(id + ".json");
        Files.delete(deckPath); Files.createDirectory(deckPath);
        Path blocker = deckPath.resolve("blocker"); Files.writeString(blocker, "Simulated disk target failure");
        assertThrows(java.io.IOException.class, () -> f.store().move(id, editor, "Commander", before.metadataRevision(), f.c().id(), "Main", f.a().id()));
        assertEquals(editor.snapshot().revision(), revision);
        assertEquals(count(editor, "First", "Main"), 1); assertEquals(count(editor, "Candidate", "Main"), 0);
        assertTrue(Files.exists(f.path().resolve("workshop").resolve(id + ".journal")));
        assertThrows(IllegalStateException.class, () -> f.store().assertWritable(id));
        assertThrows(IllegalStateException.class, () -> f.store().metadata(id, editor.snapshot(), "Commander", patch(Map.of("plan", "Must not overwrite intent"))));
        Files.delete(blocker); Files.delete(deckPath);
        var recovered = new DeckWorkshopStore(f.path(), f.catalog());
        var saved = JsonParser.parseString(Files.readString(deckPath)).getAsJsonObject();
        var reopened = new DeckEditor(f.catalog(), new Deck(saved.get("name").getAsString()));
        reopened.apply(0, List.of(JSON.fromJson(saved.get("cards"), DeckEditor.Edit[].class)));
        assertEquals(count(reopened, "First", "Main"), 0); assertEquals(count(reopened, "Candidate", "Main"), 1);
        assertTrue(recovered.view(id, reopened.snapshot(), "Commander").considering().stream().anyMatch(row -> row.cardId().equals(f.a().id())));
    }

    @Test public void reopeningWithAResetRevisionCannotMakeAStaleSourcePreviewValidAgain() throws Exception {
        var f = fixture(); var result = imported(f); var editor = result.editor(); String id = result.deckId();
        var preview = f.store().stage(remote(f, 3, 1, 1, "B"), id, editor.snapshot(), "Commander");
        assertEquals(editor.snapshot().revision(), 0L);
        editor.apply(0, List.of(new DeckEditor.Edit("Main", f.b().id(), 2)));
        var reopened = new DeckEditor(f.catalog(), editor.toDeck());
        assertEquals(reopened.snapshot().revision(), 0L);
        assertThrows(IllegalArgumentException.class, () -> f.store().apply(preview.token(), id, reopened, "Commander", "remote"));
        assertEquals(count(reopened, "Second", "Main"), 2);
    }

    @Test public void gameplayIdentityIncludesAllDescribedRulesButExcludesPrintingAndArt() throws Exception {
        var f = fixture();
        var base = JSON.toJsonTree(f.a()).getAsJsonObject();
        String original = fingerprint(base);
        for (var change : Map.of("manaCost", "3 G", "manaValue", "4", "type", "Instant", "power", "7", "toughness", "8", "colors", "1", "colorIdentity", "3").entrySet()) {
            var altered = base.deepCopy(); altered.addProperty(change.getKey(), change.getValue());
            assertNotEquals(fingerprint(altered), original, change.getKey() + " is gameplay evidence, not a printing choice");
        }
        for (var change : Map.of("id", "alternate-printing", "edition", "ALT", "collectorNumber", "123", "artName", "Different art", "artFace", "back").entrySet()) {
            var altered = base.deepCopy(); altered.addProperty(change.getKey(), change.getValue());
            assertEquals(fingerprint(altered), original, change.getKey() + " must not discard gameplay evidence");
        }
        var foil = base.deepCopy(); foil.addProperty("foil", true); assertEquals(fingerprint(foil), original);
        var back = JSON.toJsonTree(new CardCatalog.FaceInfo("Back", "2 G", "Creature Elf", "Vigilance", "3", "3", "Front art", "back")).getAsJsonObject();
        var doubleFaced = base.deepCopy(); doubleFaced.add("otherFace", back);
        String withBack = fingerprint(doubleFaced); assertNotEquals(withBack, original);
        back.addProperty("artName", "Different back art"); assertEquals(fingerprint(doubleFaced), withBack);
        back.addProperty("power", "4"); assertNotEquals(fingerprint(doubleFaced), withBack);
    }

    private static String fingerprint(JsonObject card) {
        return DeckWorkshopStore.fingerprint(new DeckEditor.Snapshot("Example", 0,
                List.of(new DeckEditor.Entry("Main", JSON.fromJson(card, CardCatalog.CardInfo.class), 1)), false, false), "Commander");
    }

    @Test public void sourceUrlParsingOnlyAllowsThePublicProviderAndKnownShape() {
        assertEquals(ArchidektSource.deckId("https://archidekt.com/decks/123/some_name?x=1#cards"), "123");
        assertEquals(ArchidektSource.deckId("archidekt:123"), "123");
        for (String bad : List.of("http://archidekt.com/decks/123", "https://archidekt.com.evil.test/decks/123", "https://evil.test/decks/123",
                "https://archidekt.com@evil.test/decks/123", "https://archidekt.com:123/decks/123", "https://archidekt.com/api/decks/123", "https://archidekt.com/decks/0"))
            assertThrows(IllegalArgumentException.class, () -> ArchidektSource.deckId(bad));
    }

    @Test public void commanderVariantsCannotSilentlyImportWithRegularCommanderRules() throws Exception {
        var f = fixture();
        for (int code : List.of(11, 12)) {
            var source = remote(f, 1, 1, 1, "A").original().deepCopy(); source.addProperty("deckFormat", code);
            var parsed = ArchidektSource.parse("123", source, f.catalog());
            assertEquals(parsed.format(), "Constructed", "Only Archidekt's regular Commander code 3 selects Commander rules");
            assertTrue(parsed.problems().stream().anyMatch(problem -> problem.contains(code == 11 ? "1v1 Commander" : "Duel Commander") && problem.contains("not supported")));
            assertEquals(parsed.cards().size(), 5, "Unsupported-format feedback must preserve the reviewed card list");
            var preview = f.store().stage(parsed, null, null, null);
            assertThrows(IllegalArgumentException.class, () -> f.store().apply(preview.token(), null, null, "Constructed", "remote"));
            assertTrue(f.editor().snapshot().entries().isEmpty());
        }
        var commander = remote(f, 1, 1, 1, "A"); assertEquals(commander.format(), "Commander"); assertTrue(commander.problems().isEmpty());
        var standard = commander.original().deepCopy(); standard.addProperty("deckFormat", 1);
        var constructed = ArchidektSource.parse("123", standard, f.catalog());
        assertEquals(constructed.format(), "Constructed"); assertTrue(constructed.problems().isEmpty());
    }
}
