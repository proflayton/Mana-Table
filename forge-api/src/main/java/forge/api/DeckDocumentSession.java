package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckSection;

import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Owns the open document and its editing, persistence and attached practice lifetime. */
final class DeckDocumentSession {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    record RequestScope(String deckId, Long revision) { }
    record DocumentScope(String deckId, long revision) { }
    record SavedDeck(Deck deck, String format) { }

    private static final class Practice {
        final List<CardCatalog.CardInfo> library = new ArrayList<>();
        final List<CardCatalog.CardInfo> hand = new ArrayList<>();
        int mulligans, draws;
        String testId;
    }
    private static final class ActiveDocument {
        final String id;
        final DeckEditor editor;
        String format, saveError;
        Practice practice = new Practice();
        ActiveDocument(String id, DeckEditor editor, String format) {
            this.id = id; this.editor = editor; this.format = format;
        }
        void resetPractice() { practice = new Practice(); }
    }

    /** Detached match contents and their exact originating document boundary. */
    static final class PreparedDeck {
        private final ActiveDocument source;
        private final DeckEditor.Snapshot snapshot;
        private final String format, saveError;
        private final MatchSetup.Prepared prepared;
        private final boolean commanderAvailable;
        private PreparedDeck(ActiveDocument source, MatchSetup.Prepared prepared) {
            this.source = source; snapshot = source.editor.snapshot(); format = source.format;
            saveError = source.saveError; this.prepared = prepared;
            commanderAvailable = format.equals("Constructed") && MatchSetup.suggestedFormat(source.editor.toDeck()).equals("Commander");
        }
        DocumentScope scope() { return new DocumentScope(source.id, snapshot.revision()); }
        Deck deck() { return prepared.deck(); }
        String format() { return format; }
        MatchSetup.Preview preview() { return prepared.preview(); }
    }

    private record StoredDeck(int version, String name, String format, List<DeckEditor.Edit> cards, long updated, Map<String, String> tokenPrintings) { }
    private record LoadedDeck(DeckEditor editor, String format) { }
    private record DeletedDeck(String deckId, String name, String restoreToken, long deletedAt) { }

    private final CardCatalog catalog;
    private final Path directory;
    private final DeckWorkshopStore workshop;
    private final DeckLibraryTrash removedDecks;
    private final DeckAnalysisStore analysis;
    private final DeckAnalysisFormats analysisFormats;
    private final DeckPlaytests playtests;
    private ActiveDocument active;

    DeckDocumentSession(CardCatalog catalog, Path directory, DeckPlaytests playtests) throws Exception {
        this.catalog = catalog; this.directory = directory; this.playtests = playtests;
        removedDecks = new DeckLibraryTrash(directory);
        workshop = new DeckWorkshopStore(directory, catalog);
        analysis = new DeckAnalysisStore(directory, catalog, workshop);
        analysisFormats = new DeckAnalysisFormats(catalog);
    }

    void loadAnalysisFormats(Path resources) { analysisFormats.load(resources); }

    Object create(String name, String requestedFormat) {
        String checkedName = checkedName(name), format = checkedFormat(requestedFormat);
        ensureSaved();
        active = new ActiveDocument(UUID.randomUUID().toString(), new DeckEditor(catalog, new Deck(checkedName)), format);
        return save(active);
    }

    Object open(String id) throws Exception {
        ensureSaved();
        var loaded = loadStoredDeck(id);
        String format = checkedFormat(loaded.format());
        var candidate = new ActiveDocument(id, new DeckEditor(catalog, loaded.editor().toDeck()), format);
        workshop.ensureVersion(candidate.id, candidate.editor.snapshot(), candidate.format);
        active = candidate;
        return state();
    }

    Object duplicate(RequestScope scope, String name) throws Exception {
        var document = requireOptionalScope(scope);
        var copy = new Deck(document.editor.toDeck(), checkedName(name));
        ensureSaved();
        var editor = new DeckEditor(catalog, copy);
        String id = UUID.randomUUID().toString();
        workshop.duplicate(document.id, id, editor.snapshot(), document.format);
        active = new ActiveDocument(id, editor, document.format);
        return save(active);
    }

    Object importPreset(String id) {
        var deck = DeckPresets.create(id);
        ensureSaved();
        active = new ActiveDocument(UUID.randomUUID().toString(), new DeckEditor(catalog, deck), "Commander");
        return save(active);
    }

    Object importPreview(String text) {
        var preview = DeckImport.preview(text);
        String suggested = preview.problems().isEmpty() && !preview.entries().isEmpty()
                ? MatchSetup.suggestedFormat(preview.open("Import preview", catalog).toDeck()) : "Constructed";
        return Map.of("entries", preview.entries(), "problems", preview.problems(), "warnings", preview.warnings(), "suggestedFormat", suggested);
    }

    Object importDeck(String name, String text, String requestedFormat) {
        var preview = DeckImport.preview(text);
        var editor = preview.open(checkedName(name), catalog);
        if (preview.entries().isEmpty()) throw new IllegalArgumentException("Paste at least one card");
        String format = requestedFormat.equals("Auto") ? MatchSetup.suggestedFormat(editor.toDeck()) : checkedFormat(requestedFormat);
        ensureSaved();
        active = new ActiveDocument(UUID.randomUUID().toString(), editor, format);
        return save(active);
    }

    Object setCommanders(RequestScope scope, List<String> ids) throws Exception {
        var document = requireDeck();
        workshop.assertWritable(document.id);
        if (!document.id.equals(scope.deckId())) throw new IllegalArgumentException("The selected deck changed. Reopen the deck before editing.");
        String beforeToken = document.editor.historyToken();
        DeckCommanders.set(document.editor, scope.revision(), ids);
        workshop.commanderFormatTransition(document.id, beforeToken, document.editor, document.format);
        document.format = "Commander";
        return save(document);
    }

    Object edit(RequestScope scope, List<DeckEditor.Edit> edits) {
        var document = requireOptionalScope(scope);
        workshop.assertWritable(document.id);
        document.editor.apply(scope.revision(), edits);
        return save(document);
    }

    Object rename(RequestScope scope, String name) {
        var document = requireOptionalScope(scope);
        workshop.assertWritable(document.id);
        document.editor.rename(scope.revision(), name);
        return save(document);
    }

    Object undo(RequestScope scope) throws Exception { return history(scope, "undo"); }
    Object redo(RequestScope scope) throws Exception { return history(scope, "redo"); }
    private Object history(RequestScope scope, String action) throws Exception {
        var document = requireOptionalScope(scope);
        document.format = workshop.history(document.id, document.editor, document.format, action, scope.revision()).format();
        return save(document);
    }

    Object changeFormat(RequestScope scope, String requestedFormat) throws Exception {
        var document = requireOptionalScope(scope);
        if (scope.revision() == null || scope.revision() != document.editor.snapshot().revision())
            throw new IllegalArgumentException("Deck changed; refresh before changing format");
        document.format = workshop.format(document.id, document.editor, document.format, checkedFormat(requestedFormat)).format();
        return save(document);
    }

    Object save(RequestScope scope) { return save(requireOptionalScope(scope)); }

    Object state() {
        var document = requireDeck();
        var result = new LinkedHashMap<String, Object>();
        result.put("id", document.id);
        try { result.put("libraryToken", DeckLibraryTrash.token(removedDecks.readSaved(document.id))); }
        catch (java.io.IOException error) { result.put("libraryToken", ""); }
        result.put("format", document.format); result.put("deck", document.editor.snapshot());
        result.put("validation", document.editor.validate(DeckFormat.valueOf(document.format)));
        result.put("commanderChoices", DeckCommanders.choices(document.editor.toDeck()));
        result.put("tokenPreviews", DeckTokens.view(document.id, document.editor).tokens().stream().map(DeckTokens.Token::card).toList());
        result.put("saveError", document.saveError);
        return result;
    }

    private Object save(ActiveDocument document) {
        try {
            workshop.assertWritable(document.id);
            var snapshot = document.editor.snapshot();
            var stored = new StoredDeck(1, snapshot.name(), document.format, snapshot.entries().stream()
                    .map(entry -> new DeckEditor.Edit(entry.section(), entry.card().id(), entry.quantity())).toList(), System.currentTimeMillis(), document.editor.toDeck().getTokenPrintings());
            Path target = file(document.id), temporary = directory.resolve(document.id + ".tmp");
            Files.writeString(temporary, JSON.toJson(stored), StandardCharsets.UTF_8);
            try { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE); }
            catch (AtomicMoveNotSupportedException error) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
            workshop.ensureVersion(document.id, snapshot, document.format);
            document.saveError = null;
        } catch (Exception error) { document.saveError = error.getMessage(); }
        return state();
    }

    String export(RequestScope scope, String kind) {
        var document = requireOptionalScope(scope);
        var result = new StringBuilder();
        if (kind.equals("forge")) {
            result.append("[metadata]\nName=").append(document.editor.snapshot().name().replaceAll("[\\r\\n]", " ")).append("\n");
            for (var section : document.editor.toDeck()) {
                if (!section.getValue().isEmpty()) result.append('[').append(section.getKey().name()).append("]\n")
                        .append(section.getValue().toCardList("\n")).append('\n');
            }
        } else for (DeckSection section : DeckSection.values()) {
            var entries = document.editor.snapshot().entries().stream().filter(entry -> entry.section().equals(section.name())).toList();
            if (entries.isEmpty()) continue;
            result.append(section == DeckSection.Main ? "Deck" : section.name()).append('\n');
            for (var entry : entries) {
                var card = entry.card();
                if (forge.item.IPaperCard.NO_COLLECTOR_NUMBER.equals(card.collectorNumber()) || card.collectorNumber().isBlank()) {
                    result.append(entry.quantity()).append(' ').append(forge.card.CardDb.CardRequest.compose(
                            forge.card.CardDb.CardRequest.compose(card.name(), card.foil()), card.edition(), card.artIndex())).append('\n');
                    continue;
                }
                result.append(entry.quantity()).append(' ').append(card.name()).append(" (").append(card.edition()).append(')');
                result.append(' ').append(card.collectorNumber());
                if (card.foil()) result.append(" *F*");
                result.append('\n');
            }
            result.append('\n');
        }
        return result.toString();
    }

    Object insights() {
        var document = requireDeck();
        return DeckInsights.analyze(catalog, document.editor.snapshot(), document.format, document.id);
    }
    DeckWorkshopStore.View workshop(RequestScope scope) throws Exception {
        var document = requireWorkshop(scope);
        return workshop.view(document.id, document.editor.snapshot(), document.format);
    }
    Object updateWorkshop(RequestScope scope, JsonObject patch) throws Exception {
        var document = requireWorkshop(scope);
        return workshop.metadata(document.id, document.editor.snapshot(), document.format, patch);
    }
    Object restoreVersion(RequestScope scope, String versionId) throws Exception {
        var document = requireWorkshop(scope);
        document.format = workshop.restore(document.id, document.editor, document.format, versionId).format();
        document.resetPractice(); return save(document);
    }
    Object moveWorkshopCard(RequestScope scope, long metadataRevision, String cardId, String to, String cutCardId) throws Exception {
        var document = requireWorkshop(scope);
        workshop.move(document.id, document.editor, document.format, metadataRevision, cardId, to, cutCardId);
        document.resetPractice(); return save(document);
    }
    Object replacePrinting(RequestScope scope, String section, String cardId, String printingId) throws Exception {
        var document = requireWorkshop(scope);
        workshop.replacePrinting(document.id, document.editor, document.format, section, cardId, printingId);
        document.resetPractice(); return save(document);
    }
    DeckTokens.View tokens(RequestScope scope) {
        var document = requireWorkshop(scope);
        return DeckTokens.view(document.id, document.editor);
    }
    CardCatalog.Page tokenPrintings(RequestScope scope, String tokenId, int offset, int limit) {
        var document = requireWorkshop(scope);
        return DeckTokens.printings(document.editor.toDeck(), tokenId, offset, limit);
    }
    Object setTokenPrinting(RequestScope scope, String tokenId, String printingId) {
        var document = requireWorkshop(scope);
        document.editor.setTokenPrinting(scope.revision(), tokenId, printingId);
        return save(document);
    }
    Object previewSource(String url) throws Exception { return workshop.importPreview(url); }
    Object refreshSource(RequestScope scope) throws Exception {
        var document = requireWorkshop(scope);
        return workshop.refresh(document.id, document.editor.snapshot(), document.format);
    }
    Object applySource(RequestScope scope, String token, String choice) throws Exception {
        ensureSaved();
        ActiveDocument target = scope.deckId() == null ? null : requireWorkshop(scope);
        var applied = workshop.apply(token, scope.deckId(), target == null ? null : target.editor,
                active == null ? "Constructed" : active.format, choice);
        active = new ActiveDocument(applied.deckId(), applied.editor(), applied.format());
        return save(active);
    }
    Object inspectTest(RequestScope scope, String testId) throws Exception {
        var document = requireWorkshop(scope);
        return playtests.inspect(document.id, testId, document.editor.snapshot(), document.format);
    }
    Object inspectVersion(RequestScope scope, String versionId) throws Exception {
        var document = requireWorkshop(scope);
        return workshop.inspect(document.id, versionId, document.editor.snapshot(), document.format);
    }
    Object practiceHand(RequestScope scope, String action, Integer index, String question) throws Exception {
        var document = requireWorkshop(scope);
        if (action.equals("new")) {
            var snapshot = document.editor.snapshot();
            var sample = playtests.newHand(document.id, workshop.ensureVersion(document.id, snapshot, document.format),
                    snapshot.name(), document.format, snapshot.entries(), question);
            document.practice.testId = sample.testId(); return sample;
        }
        var sample = playtests.activeHand(document.id);
        if (sample == null || document.practice.testId == null || !document.practice.testId.equals(sample.testId()))
            throw new IllegalArgumentException("Start a new opening hand first");
        return playtests.practice(document.practice.testId, action, index);
    }

    DeckAnalysisStore.View analyze(RequestScope scope) throws Exception {
        var document = requireWorkshop(scope);
        return analysis.view(document.id, document.editor.snapshot(), document.format, analysisFormats);
    }
    Object saveAnalysisProfile(RequestScope scope, long metadataRevision, String fingerprint, JsonObject profile) throws Exception {
        var document = requireWorkshop(scope);
        return analysis.profile(document.id, document.editor.snapshot(), document.format, metadataRevision, fingerprint, profile, analysisFormats);
    }
    Object reviewAnalysis(RequestScope scope, long metadataRevision, String fingerprint, String recommendationId, String decision, String note) throws Exception {
        var document = requireWorkshop(scope);
        return analysis.review(document.id, document.editor.snapshot(), document.format, metadataRevision, fingerprint, recommendationId, decision, note, analysisFormats);
    }
    Object considerAnalysis(RequestScope scope, long metadataRevision, String fingerprint, String recommendationId, String note) throws Exception {
        var document = requireWorkshop(scope);
        var reviewed = analysis.review(document.id, document.editor.snapshot(), document.format, metadataRevision, fingerprint, recommendationId, "consider", note, analysisFormats);
        return Map.of("analysis", reviewed, "workshop", workshop.view(document.id, document.editor.snapshot(), document.format));
    }

    Object practice(String action, int index) {
        var document = requireDeck();
        var practice = document.practice;
        switch (action) {
            case "shuffle", "mulligan" -> {
                if (action.equals("shuffle")) practice.mulligans = 0; else practice.mulligans++;
                practice.library.clear(); practice.hand.clear(); practice.draws = 0;
                for (var entry : document.editor.snapshot().entries()) if (entry.section().equals("Main"))
                    for (int i = 0; i < entry.quantity(); i++) practice.library.add(entry.card());
                if (practice.library.size() < 7) throw new IllegalArgumentException("Add at least 7 main-deck cards to practice");
                Collections.shuffle(practice.library);
                for (int i = 0; i < 7; i++) practice.hand.add(practice.library.remove(practice.library.size() - 1));
            }
            case "draw" -> { if (!practice.library.isEmpty()) { practice.hand.add(practice.library.remove(practice.library.size() - 1)); practice.draws++; } }
            case "bottom" -> {
                if (index < 0 || index >= practice.hand.size()) throw new IllegalArgumentException("Choose a card from your hand");
                practice.library.add(0, practice.hand.remove(index));
            }
            default -> throw new IllegalArgumentException("Unknown practice action");
        }
        return Map.of("hand", List.copyOf(practice.hand), "remaining", practice.library.size(), "mulligans", practice.mulligans, "draws", practice.draws);
    }

    PreparedDeck prepareSetup(String requestedId, String commanderId) throws Exception {
        if (requestedId != null && (active == null || !active.id.equals(requestedId))) open(requestedId);
        return capture(requireDeck(), commanderId);
    }
    Object setupView(PreparedDeck prepared) {
        var result = new LinkedHashMap<String, Object>();
        result.put("deckId", prepared.scope().deckId()); result.put("revision", prepared.scope().revision());
        result.put("name", prepared.snapshot.name()); result.put("setup", prepared.preview());
        result.put("saveError", prepared.saveError); result.put("opponents", MatchSession.opponents(prepared.format));
        result.put("maxPlayers", prepared.format.equals("Commander") ? 6 : 2);
        result.put("commanderAvailable", prepared.commanderAvailable);
        return result;
    }
    List<Map<String, String>> opponents() { return MatchSession.opponents(active == null ? "Constructed" : active.format); }

    PreparedDeck prepareStart(RequestScope scope, String commanderId) {
        var document = requireDeck(); ensureSaved();
        if (scope.deckId() != null && !document.id.equals(scope.deckId())
                || scope.revision() != null && scope.revision() != document.editor.snapshot().revision())
            throw new IllegalArgumentException("The deck changed. Reopen match setup before starting.");
        return capture(document, commanderId);
    }
    PreparedDeck prepareTest(RequestScope scope, String commanderId) {
        var document = requireWorkshop(scope); ensureSaved();
        if (document.format.equals("Commander") && document.editor.snapshot().entries().stream().noneMatch(row -> row.section().equals("Commander") && row.quantity() > 0))
            throw new IllegalArgumentException("Set your commander in the deck before testing so the saved version matches the game.");
        return capture(document, commanderId);
    }
    DeckPlaytests.TestRecord beginGameTest(PreparedDeck prepared, List<String> opponents, String question) throws Exception {
        requireCurrent(prepared);
        return playtests.begin(prepared.scope().deckId(), workshop.ensureVersion(prepared.scope().deckId(), prepared.snapshot, prepared.format),
                prepared.snapshot.name(), prepared.format, prepared.snapshot.entries(), "game", question, opponents);
    }
    void requireCurrent(PreparedDeck prepared) {
        var document = requireDeck();
        ensureSaved();
        if (document != prepared.source || document.editor.snapshot().revision() != prepared.scope().revision())
            throw new IllegalArgumentException("The deck changed. Reopen match setup before starting.");
    }
    private PreparedDeck capture(ActiveDocument document, String commanderId) {
        var prepared = MatchSetup.prepare(document.editor.toDeck(), document.format, commanderId);
        var preview = prepared.preview();
        if (preview.problem() == null) {
            String problem = new DeckEditor(catalog, prepared.deck()).validate(DeckFormat.valueOf(document.format)).problem();
            prepared = new MatchSetup.Prepared(prepared.deck(), new MatchSetup.Preview(preview.format(), preview.startingLife(), preview.commanderId(),
                    preview.commanders(), preview.needsCommander(), preview.commanderChoices(), problem));
        }
        return new PreparedDeck(document, prepared);
    }

    SavedDeck loadLobbyDeck(String id) throws Exception {
        var loaded = loadStoredDeck(id);
        return new SavedDeck(loaded.editor().toDeck(), checkedFormat(loaded.format()));
    }
    Object delete(String id, String libraryToken) throws Exception {
        file(id); workshop.assertWritable(id);
        if (active != null && id.equals(active.id)) ensureSaved();
        var stored = JSON.fromJson(new String(removedDecks.readSaved(id), StandardCharsets.UTF_8), StoredDeck.class);
        if (stored == null || stored.version() != 1) throw new IllegalArgumentException("Unsupported deck file version");
        var removed = removedDecks.remove(id, libraryToken);
        if (active != null && id.equals(active.id)) active = null;
        return new DeletedDeck(id, stored.name(), removed.restoreToken(), removed.deletedAt());
    }
    Object deletedDecks() throws Exception {
        var result = new ArrayList<DeletedDeck>();
        for (var entry : removedDecks.entries()) {
            var stored = JSON.fromJson(Files.readString(entry.path(), StandardCharsets.UTF_8), StoredDeck.class);
            if (stored == null || stored.version() != 1) throw new IllegalArgumentException("Unsupported removed deck file version");
            result.add(new DeletedDeck(entry.deckId(), stored.name(), entry.restoreToken(), entry.deletedAt()));
        }
        return List.copyOf(result);
    }
    Object restore(String id, String restoreToken) throws Exception {
        removedDecks.restore(id, restoreToken); workshop.recoverRestoredDeck(id);
        byte[] bytes = removedDecks.readSaved(id);
        return summary(id, JSON.fromJson(new String(bytes, StandardCharsets.UTF_8), StoredDeck.class), DeckLibraryTrash.token(bytes));
    }
    Object list() throws Exception {
        var decks = new ArrayList<Map<String, Object>>(); var problems = new ArrayList<String>();
        try (var files = Files.list(directory)) {
            for (Path file : files.filter(path -> path.getFileName().toString().endsWith(".json")).toList()) {
                try {
                    String id = file.getFileName().toString().replace(".json", "");
                    byte[] bytes = removedDecks.readSaved(id);
                    var stored = JSON.fromJson(new String(bytes, StandardCharsets.UTF_8), StoredDeck.class);
                    if (stored.version() != 1) throw new IllegalArgumentException("Unsupported version");
                    decks.add(summary(id, stored, DeckLibraryTrash.token(bytes)));
                } catch (Exception error) { problems.add(file.getFileName() + ": " + error.getMessage()); }
            }
        }
        decks.sort(Comparator.comparingLong((Map<String, Object> deck) -> (long) deck.get("updated")).reversed());
        return Map.of("decks", decks, "problems", problems);
    }
    private LoadedDeck loadStoredDeck(String id) throws Exception {
        var stored = JSON.fromJson(Files.readString(file(id)), StoredDeck.class);
        if (stored.version() != 1) throw new IllegalArgumentException("Unsupported deck file version");
        var deck = new Deck(checkedName(stored.name()));
        deck.setTokenPrintings(DeckTokens.readPreferences(JSON.toJsonTree(stored.tokenPrintings())));
        var loaded = new DeckEditor(catalog, deck);
        loaded.apply(0, stored.cards()); return new LoadedDeck(loaded, stored.format());
    }
    private static Map<String, Object> summary(String id, StoredDeck stored, String libraryToken) {
        if (stored == null || stored.version() != 1) throw new IllegalArgumentException("Unsupported deck file version");
        int main = stored.cards().stream().filter(card -> card.section().equals("Main")).mapToInt(DeckEditor.Edit::quantity).sum();
        return Map.of("id", id, "name", stored.name(), "format", stored.format(), "count", main, "updated", stored.updated(), "libraryToken", libraryToken);
    }
    private ActiveDocument requireDeck() {
        if (active == null) throw new IllegalStateException("Open or create a deck first");
        workshop.assertNotRemoved(active.id); return active;
    }
    private ActiveDocument requireOptionalScope(RequestScope scope) {
        var document = requireDeck();
        // Legacy callers omit the document ID. An explicit target never selects it.
        if (scope.deckId() != null) {
            workshop.assertWritable(document.id);
            if (!document.id.equals(scope.deckId()) || scope.revision() == null || scope.revision() != document.editor.snapshot().revision())
                throw new IllegalArgumentException("The deck changed. Reopen it before editing.");
        }
        return document;
    }
    private ActiveDocument requireWorkshop(RequestScope scope) {
        var document = requireDeck(); workshop.assertWritable(document.id);
        if (!document.id.equals(scope.deckId()) || scope.revision() == null || scope.revision() != document.editor.snapshot().revision())
            throw new IllegalArgumentException("The deck changed. Refresh the workshop before continuing.");
        return document;
    }
    private void ensureSaved() {
        if (active == null) return;
        workshop.assertWritable(active.id);
        if (active.saveError != null) throw new IllegalStateException("Save the current deck before switching: " + active.saveError);
    }
    private Path file(String id) {
        if (!UUID.fromString(id).toString().equals(id)) throw new IllegalArgumentException("Invalid deck ID");
        return directory.resolve(id + ".json");
    }
    private static String checkedFormat(String value) {
        if (!List.of("Constructed", "Commander", "Limited").contains(value)) throw new IllegalArgumentException("Unsupported format");
        return value;
    }
    private static String checkedName(String value) {
        if (value == null || value.isBlank() || value.length() > 100) throw new IllegalArgumentException("Deck name must be 1..100 characters");
        return value.strip();
    }
}
