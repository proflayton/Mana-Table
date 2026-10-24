package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckSection;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintStream;
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

/** Desktop commands use private child-process pipes; multiplayer uses versioned JSON synchronization. */
public final class DesktopEngine implements AutoCloseable {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private final CardCatalog catalog;
    private final DeckAnalysisStore analysis;
    private final DeckAnalysisFormats analysisFormats;
    private final Path directory;
    private final DeckWorkshopStore workshop;
    private final DeckLibraryTrash removedDecks;
    private final DeckPlaytests playtests;
    private String handTestId;
    private DeckEditor editor;
    private String deckId;
    private String format = "Constructed";
    private String saveError;
    private final List<CardCatalog.CardInfo> library = new ArrayList<>();
    private final List<CardCatalog.CardInfo> hand = new ArrayList<>();
    private int mulligans;
    private int draws;
    private Path resources;
    private SeatConnection match;
    private SyncLobby multiplayer;
    private final Map<String, SyncLobby> localSeats = new LinkedHashMap<>();

    public DesktopEngine(CardCatalog catalog, Path directory) throws Exception {
        this.catalog = catalog;
        this.analysisFormats = new DeckAnalysisFormats(catalog);
        this.directory = directory.toAbsolutePath().normalize();
        Files.createDirectories(this.directory);
        removedDecks = new DeckLibraryTrash(this.directory);
        workshop = new DeckWorkshopStore(this.directory, catalog);
        this.analysis = new DeckAnalysisStore(this.directory, catalog, workshop);
        playtests = new DeckPlaytests(this.directory);
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 2) { throw new IllegalArgumentException("Expected resource and deck directories"); }
        PrintStream protocol = System.out;
        System.setOut(System.err); // Forge diagnostics must never corrupt the JSON protocol.
        protocol.println(JSON.toJson(Map.of("event", "loading", "message", "Loading Forge card library…")));
        var data = EngineResources.load(Path.of(args[0]));
        var engine = new DesktopEngine(CardCatalog.fromDatabases(data.getAvailableDatabases().values()), Path.of(args[1]));
        engine.resources = Path.of(args[0]);
        engine.analysisFormats.load(engine.resources);
        protocol.println(JSON.toJson(Map.of("event", "ready", "printings", engine.catalog.size(), "protocolVersion", 2, "capabilities", List.of("seat-sync", "snapshots", "command-receipts", "resume", "table-social", "deck-workshop", "deck-analysis", "source-actions", "deck-library", "deck-tokens", "semantic-priority-actions"))));
        try (engine; var input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = input.readLine()) != null) {
                JsonObject request = null;
                try {
                    if (line.length() > 2_000_000) { throw new IllegalArgumentException("Request too large"); }
                    request = JsonParser.parseString(line).getAsJsonObject();
                    Object result = engine.dispatch(request.get("method").getAsString(), request.getAsJsonObject("params"));
                    var reply = new JsonObject();
                    reply.add("id", request.get("id"));
                    reply.add("result", JSON.toJsonTree(result));
                    protocol.println(JSON.toJson(reply));
                } catch (Exception error) {
                    // Validation errors are returned to the caller; unexpected
                    // request failures also need a stack trace in engine.log.
                    if (!(error instanceof IllegalArgumentException)) error.printStackTrace(System.err);
                    var reply = new JsonObject();
                    if (request != null) { reply.add("id", request.get("id")); }
                    reply.addProperty("error", error.getMessage() == null ? error.getClass().getSimpleName() : error.getMessage());
                    protocol.println(JSON.toJson(reply));
                }
            }
        }
    }

    @Override
    public void close() {
        try { playtests.interruptActive(); }
        catch (RuntimeException error) { System.err.println("Could not finalize playtest history: " + error.getMessage()); }
        for (var seat : localSeats.values()) seat.close();
        localSeats.clear();
        if (match != null) match.close();
        if (multiplayer != null) {
            multiplayer.close();
        }
    }

    public Object dispatch(String method, JsonObject arguments) throws Exception {
        JsonObject p = arguments == null ? new JsonObject() : arguments;
        if (p.has("connectionId")) {
            var seat = localSeats.get(p.get("connectionId").getAsString());
            if (seat == null) throw new IllegalArgumentException("Unknown local seat connection");
            return switch (method) {
                case "multiplayerState" -> seat.state();
                case "multiplayerConfigure" -> seat.configure(string(p, "tableId", ""), string(p, "format", "Constructed"), number(p, "playerCount", 2));
                case "multiplayerSetSeat" -> seat.setSeat(p);
                case "tableSocial" -> seat.social(p);
                case "tableChat" -> seat.chat(p);
                case "tableName" -> seat.rename(p);
                case "multiplayerSelectDeck" -> { var loaded = loadStoredDeck(string(p, "deckId", "")); yield seat.selectDeck(loaded.editor().toDeck(), loaded.format()); }
                case "multiplayerReady" -> seat.ready(p.get("ready").getAsBoolean());
                case "multiplayerReturn" -> seat.returnToLobby();
                case "multiplayerClose" -> seat.close();
                case "matchState" -> seat.match().state();
                case "matchAction" -> seat.match().action(p);
                case "matchSourceActions" -> seat.match().sourceActions(p);
                case "matchConcede" -> seat.match().concede(p);
                default -> throw new IllegalArgumentException("Command is not available on a seat connection");
            };
        }
        // Older workshop callers omit deckId; native edits also bind the active
        // document, so a delayed action cannot edit a different opened deck.
        if (p.has("deckId") && List.of("edit", "rename", "undo", "redo", "save", "export", "duplicate").contains(method)) {
            requireDeck();
            workshop.assertWritable(deckId);
            if (!deckId.equals(string(p, "deckId", "")) || !p.has("revision") || p.get("revision").getAsLong() != editor.snapshot().revision())
                throw new IllegalArgumentException("The deck changed. Reopen it before editing.");
        }
        return switch (method) {
            case "deckAnalysis", "analysisProfile", "analysisReview", "analysisConsider" -> {
                requireWorkshopDeck(p);
                if (method.equals("deckAnalysis")) yield analysis.view(deckId, editor.snapshot(), format, analysisFormats);
                if (!p.has("metadataRevision")) throw new IllegalArgumentException("Missing analysis notes revision");
                long metadataRevision = p.get("metadataRevision").getAsLong();
                if (method.equals("analysisProfile")) yield analysis.profile(deckId, editor.snapshot(), format, metadataRevision,
                        string(p, "fingerprint", ""), p.getAsJsonObject("profile"), analysisFormats);
                if (method.equals("analysisConsider")) {
                    var reviewed = analysis.review(deckId, editor.snapshot(), format, metadataRevision, string(p, "fingerprint", ""),
                            string(p, "recommendationId", ""), "consider", string(p, "note", ""), analysisFormats);
                    yield Map.of("analysis", reviewed, "workshop", workshop.view(deckId, editor.snapshot(), format));
                }
                yield analysis.review(deckId, editor.snapshot(), format, metadataRevision, string(p, "fingerprint", ""),
                        string(p, "recommendationId", ""), string(p, "decision", ""), string(p, "note", ""), analysisFormats);
            }
            case "testMatchViews" -> multiplayer().testViews();
            case "multiplayerLocalJoin" -> { var id = UUID.randomUUID().toString(); localSeats.put(id, multiplayer().localGuest()); yield Map.of("connectionId", id); }
            case "search" -> catalog.browse(new CardCatalog.Query(string(p, "text", ""),
                    integer(p, "colors"), integer(p, "maxManaValue"), number(p, "offset", 0), number(p, "limit", 48)),
                    string(p, "type", ""), string(p, "sort", "name"), !p.has("unique") || p.get("unique").getAsBoolean(),
                    integer(p, "colorIdentity"), string(p, "role", ""));
            case "cardPrintings" -> catalog.printings(string(p, "cardId", ""), number(p, "offset", 0), number(p, "limit", 6),
                    string(p, "text", ""), string(p, "finish", ""));
            case "deckTokens" -> { requireWorkshopDeck(p); yield DeckTokens.view(deckId, editor); }
            case "tokenPrintings" -> {
                requireWorkshopDeck(p);
                yield DeckTokens.printings(editor.toDeck(), string(p, "tokenId", ""), number(p, "offset", 0), number(p, "limit", 4));
            }
            case "setTokenPrinting" -> {
                requireWorkshopDeck(p); workshop.assertWritable(deckId);
                editor.setTokenPrinting(p.get("revision").getAsLong(), string(p, "tokenId", ""),
                        p.has("printingId") && !p.get("printingId").isJsonNull() ? p.get("printingId").getAsString() : null);
                yield save();
            }
            case "replacePrinting" -> {
                requireWorkshopDeck(p);
                workshop.replacePrinting(deckId, editor, format, string(p, "section", ""),
                        string(p, "cardId", ""), string(p, "printingId", ""));
                clearPractice(); yield save();
            }
            case "deckInsights" -> { requireDeck(); yield DeckInsights.analyze(catalog, editor.snapshot(), format, deckId); }
            case "workshop" -> { requireWorkshopDeck(p); yield workshop.view(deckId, editor.snapshot(), format); }
            case "workshopMetadata" -> { requireWorkshopDeck(p); yield workshop.metadata(deckId, editor.snapshot(), format, p); }
            case "workshopRestore" -> {
                requireWorkshopDeck(p);
                var applied = workshop.restore(deckId, editor, format, string(p, "versionId", ""));
                format = applied.format(); clearPractice(); yield save();
            }
            case "workshopMove" -> {
                requireWorkshopDeck(p);
                workshop.move(deckId, editor, format, p.get("metadataRevision").getAsLong(), string(p, "cardId", ""),
                        string(p, "to", ""), string(p, "cutCardId", ""));
                clearPractice(); yield save();
            }
            case "workshopSourcePreview" -> workshop.importPreview(string(p, "url", ""));
            case "workshopSourceRefresh" -> { requireWorkshopDeck(p); yield workshop.refresh(deckId, editor.snapshot(), format); }
            case "workshopSourceApply" -> {
                ensureSaved();
                String targetId = string(p, "deckId", null);
                if (targetId != null) requireWorkshopDeck(p);
                var applied = workshop.apply(string(p, "token", ""), targetId, targetId == null ? null : editor, format, string(p, "choice", "remote"));
                editor = applied.editor(); deckId = applied.deckId(); format = applied.format(); clearPractice(); yield save();
            }
            case "workshopTests" -> playtests.list(string(p, "deckId", ""));
            case "workshopInspectTest" -> {
                requireWorkshopDeck(p);
                yield playtests.inspect(deckId, string(p, "testId", ""), editor.snapshot(), format);
            }
            case "workshopInspectVersion" -> {
                requireWorkshopDeck(p);
                yield workshop.inspect(deckId, string(p, "versionId", ""), editor.snapshot(), format);
            }
            case "workshopTestNote" -> playtests.note(string(p, "deckId", ""), string(p, "testId", ""), string(p, "note", ""));
            case "workshopPractice" -> {
                requireWorkshopDeck(p);
                String action = string(p, "action", "new");
                if (action.equals("new")) {
                    var snapshot = editor.snapshot();
                    var sample = playtests.newHand(deckId, workshop.ensureVersion(deckId, snapshot, format), snapshot.name(), format, snapshot.entries(), string(p, "question", ""));
                    handTestId = sample.testId(); yield sample;
                }
                var active = playtests.activeHand(deckId);
                if (active == null || handTestId == null || !handTestId.equals(active.testId())) throw new IllegalArgumentException("Start a new opening hand first");
                yield playtests.practice(handTestId, action, integer(p, "index"));
            }
            case "workshopStartTest" -> {
                requireWorkshopDeck(p); ensureSaved();
                if (format.equals("Commander") && editor.snapshot().entries().stream().noneMatch(row -> row.section().equals("Commander") && row.quantity() > 0))
                    throw new IllegalArgumentException("Set your commander in the deck before testing so the saved version matches the game.");
                validateMatchStart(p);
                var snapshot = editor.snapshot();
                var opponents = opponentIds(p);
                var record = playtests.begin(deckId, workshop.ensureVersion(deckId, snapshot, format), snapshot.name(), format,
                        snapshot.entries(), "game", string(p, "question", ""), opponents);
                try { startMatch(p, opponents, record.id()); }
                catch (Exception error) { playtests.finish(record.id(), "error", error.getMessage()); throw error; }
                yield playtests.get(deckId, record.id());
            }
            case "list" -> list();
            case "deletedDecks" -> deletedDecks();
            case "deleteDeck" -> deleteDeck(string(p, "deckId", ""), string(p, "libraryToken", ""));
            case "restoreDeck" -> restoreDeck(string(p, "deckId", ""), string(p, "restoreToken", ""));
            case "new" -> create(string(p, "name", "Untitled deck"), string(p, "format", "Constructed"));
            case "open" -> open(string(p, "id", ""));
            case "duplicate" -> {
                requireDeck();
                var copy = new Deck(editor.toDeck(), checkedName(string(p, "name", "")));
                ensureSaved();
                var copiedEditor = new DeckEditor(catalog, copy);
                String copiedId = UUID.randomUUID().toString();
                workshop.duplicate(deckId, copiedId, copiedEditor.snapshot(), format);
                editor = copiedEditor;
                deckId = copiedId;
                clearPractice();
                yield save();
            }
            case "snapshot" -> state();
            case "setCommanders" -> {
                requireDeck();
                workshop.assertWritable(deckId);
                if (!deckId.equals(string(p, "deckId", ""))) throw new IllegalArgumentException("The selected deck changed. Reopen the deck before editing.");
                var ids = JSON.fromJson(p.get("cardIds"), String[].class);
                String beforeToken = editor.historyToken();
                DeckCommanders.set(editor, p.get("revision").getAsLong(), List.of(ids));
                workshop.commanderFormatTransition(deckId, beforeToken, editor, format);
                format = "Commander";
                yield save();
            }
            case "edit" -> {
                requireDeck();
                workshop.assertWritable(deckId);
                var edits = JSON.fromJson(p.get("edits"), DeckEditor.Edit[].class);
                editor.apply(p.get("revision").getAsLong(), List.of(edits));
                yield save();
            }
            case "rename" -> {
                requireDeck();
                workshop.assertWritable(deckId);
                editor.rename(p.get("revision").getAsLong(), string(p, "name", ""));
                yield save();
            }
            case "undo" -> { requireDeck(); format = workshop.history(deckId, editor, format, "undo", p.get("revision").getAsLong()).format(); yield save(); }
            case "redo" -> { requireDeck(); format = workshop.history(deckId, editor, format, "redo", p.get("revision").getAsLong()).format(); yield save(); }
            case "format" -> {
                requireDeck();
                if (p.get("revision").getAsLong() != editor.snapshot().revision()) {
                    throw new IllegalArgumentException("Deck changed; refresh before changing format");
                }
                format = workshop.format(deckId, editor, format, checkedFormat(string(p, "format", "Constructed"))).format();
                yield save();
            }
            case "save" -> { requireDeck(); yield save(); }
            case "deckPresets" -> DeckPresets.list();
            case "presetImport" -> {
                var deck = DeckPresets.create(string(p, "id", ""));
                ensureSaved();
                editor = new DeckEditor(catalog, deck);
                deckId = UUID.randomUUID().toString();
                format = "Commander";
                clearPractice();
                yield save();
            }
            case "importPreview" -> {
                var preview = DeckImport.preview(string(p, "text", ""));
                String suggested = preview.problems().isEmpty() && !preview.entries().isEmpty()
                        ? MatchSetup.suggestedFormat(preview.open("Import preview", catalog).toDeck()) : "Constructed";
                yield Map.of("entries", preview.entries(), "problems", preview.problems(), "warnings", preview.warnings(), "suggestedFormat", suggested);
            }
            case "import" -> {
                var preview = DeckImport.preview(string(p, "text", ""));
                DeckEditor imported = preview.open(checkedName(string(p, "name", "Imported deck")), catalog);
                if (preview.entries().isEmpty()) { throw new IllegalArgumentException("Paste at least one card"); }
                String requestedFormat = string(p, "format", "Auto");
                String importedFormat = requestedFormat.equals("Auto") ? MatchSetup.suggestedFormat(imported.toDeck()) : checkedFormat(requestedFormat);
                ensureSaved();
                editor = imported;
                deckId = UUID.randomUUID().toString();
                format = importedFormat;
                clearPractice();
                yield save();
            }
            case "export" -> export(string(p, "kind", "text"));
            case "practice" -> practice(string(p, "action", "shuffle"), number(p, "index", -1));
            case "multiplayerHost" -> multiplayer().host(string(p, "format", "Constructed"), number(p, "playerCount", 2),
                    p.has("autoPortForward") && p.get("autoPortForward").getAsBoolean(), string(p, "transport", "mixed"));
            case "multiplayerJoin" -> multiplayer().join(string(p, "address", ""));
            case "multiplayerConfigure" -> multiplayer().configure(string(p, "tableId", ""), string(p, "format", "Constructed"), number(p, "playerCount", 2));
            case "multiplayerSetSeat" -> multiplayer().setSeat(p);
            case "multiplayerSelectDeck" -> {
                var loaded = loadStoredDeck(string(p, "deckId", ""));
                yield multiplayer().selectDeck(loaded.editor().toDeck(), checkedFormat(loaded.format()));
            }
            case "multiplayerReady" -> multiplayer().ready(p.has("ready") && p.get("ready").getAsBoolean());
            case "multiplayerStart" -> multiplayer().start();
            case "multiplayerState" -> multiplayer().state();
            case "tableSocial" -> multiplayer().social(p);
            case "tableChat" -> multiplayer().chat(p);
            case "tableName" -> multiplayer().rename(p);
            case "multiplayerReconnect" -> multiplayer().reconnect();
            case "multiplayerReturn" -> multiplayer().returnToLobby();
            case "multiplayerClose" -> multiplayer().close();
            case "matchOpponents" -> MatchSession.opponents(format);
            case "matchSetup" -> {
                requireDeck();
                var preview = preparedMatch(p).preview();
                var result = new LinkedHashMap<String, Object>();
                result.put("deckId", deckId); result.put("revision", editor.snapshot().revision());
                result.put("name", editor.snapshot().name()); result.put("setup", preview);
                result.put("saveError", saveError); result.put("opponents", MatchSession.opponents(format));
                result.put("maxPlayers", format.equals("Commander") ? 6 : 2);
                result.put("commanderAvailable", format.equals("Constructed") && MatchSetup.suggestedFormat(editor.toDeck()).equals("Commander"));
                yield result;
            }
            case "matchStart" -> {
                requireDeck();
                ensureSaved();
                startMatch(p, opponentIds(p), null);
                yield match.state();
            }
            case "matchState" -> {
                if (multiplayer != null && multiplayer.connected()) {
                    yield multiplayer.match().state();
                }
                yield match == null ? null : match.state();
            }
            case "matchAction" -> {
                if (multiplayer != null && multiplayer.connected()) {
                    yield multiplayer.match().action(p);
                }
                if (match == null) throw new IllegalStateException("No active match");
                yield match.action(p);
            }
            case "matchSourceActions" -> {
                if (multiplayer != null && multiplayer.connected()) yield multiplayer.match().sourceActions(p);
                if (match == null) throw new IllegalStateException("No active match");
                yield match.sourceActions(p);
            }
            case "matchConcede" -> {
                if (multiplayer != null && multiplayer.connected()) {
                    yield multiplayer.match().concede(p);
                }
                if (match == null) throw new IllegalStateException("No active match");
                yield match.concede(p);
            }
            default -> throw new IllegalArgumentException("Unknown engine command");
        };
    }

    private Object create(String name, String requestedFormat) throws Exception {
        String newName = checkedName(name);
        String newFormat = checkedFormat(requestedFormat);
        ensureSaved();
        editor = new DeckEditor(catalog, new Deck(newName));
        deckId = UUID.randomUUID().toString();
        format = newFormat;
        clearPractice();
        return save();
    }

    private Object open(String id) throws Exception {
        ensureSaved();
        var loaded = loadStoredDeck(id);
        String loadedFormat = checkedFormat(loaded.format());
        editor = new DeckEditor(catalog, loaded.editor().toDeck());
        deckId = id;
        format = loadedFormat;
        saveError = null;
        clearPractice();
        workshop.ensureVersion(deckId, editor.snapshot(), format);
        return state();
    }

    private record StoredDeck(int version, String name, String format, List<DeckEditor.Edit> cards, long updated, Map<String, String> tokenPrintings) { }
    private record LoadedDeck(DeckEditor editor, String format) { }
    private record DeletedDeck(String deckId, String name, String restoreToken, long deletedAt) { }

    private Object deleteDeck(String id, String libraryToken) throws Exception {
        requireIdleDeckLibrary();
        file(id);
        workshop.assertWritable(id);
        if (id.equals(deckId)) ensureSaved();
        var stored = JSON.fromJson(new String(removedDecks.readSaved(id), StandardCharsets.UTF_8), StoredDeck.class);
        if (stored == null || stored.version() != 1) throw new IllegalArgumentException("Unsupported deck file version");
        var removed = removedDecks.remove(id, libraryToken);
        if (id.equals(deckId)) {
            editor = null; deckId = null; saveError = null; format = "Constructed"; clearPractice();
        }
        return new DeletedDeck(id, stored.name(), removed.restoreToken(), removed.deletedAt());
    }

    private Object deletedDecks() throws Exception {
        var result = new ArrayList<DeletedDeck>();
        for (var entry : removedDecks.entries()) {
            var stored = JSON.fromJson(Files.readString(entry.path(), StandardCharsets.UTF_8), StoredDeck.class);
            if (stored == null || stored.version() != 1) throw new IllegalArgumentException("Unsupported removed deck file version");
            result.add(new DeletedDeck(entry.deckId(), stored.name(), entry.restoreToken(), entry.deletedAt()));
        }
        return List.copyOf(result);
    }

    private Object restoreDeck(String id, String restoreToken) throws Exception {
        requireIdleDeckLibrary();
        removedDecks.restore(id, restoreToken);
        workshop.recoverRestoredDeck(id);
        byte[] bytes = removedDecks.readSaved(id);
        return summary(id, JSON.fromJson(new String(bytes, StandardCharsets.UTF_8), StoredDeck.class), DeckLibraryTrash.token(bytes));
    }

    private void requireIdleDeckLibrary() {
        if (multiplayer != null && multiplayer.connected() || localSeats.values().stream().anyMatch(SyncLobby::connected))
            throw new IllegalStateException("Leave the current lobby before deleting or restoring a saved deck.");
        if (match != null && !match.finished())
            throw new IllegalStateException("Finish or concede the current game before deleting or restoring a saved deck.");
    }

    private LoadedDeck loadStoredDeck(String id) throws Exception {
        var stored = JSON.fromJson(Files.readString(file(id)), StoredDeck.class);
        if (stored.version() != 1) { throw new IllegalArgumentException("Unsupported deck file version"); }
        var deck = new Deck(checkedName(stored.name()));
        deck.setTokenPrintings(DeckTokens.readPreferences(JSON.toJsonTree(stored.tokenPrintings())));
        var loaded = new DeckEditor(catalog, deck);
        loaded.apply(0, stored.cards());
        return new LoadedDeck(loaded, stored.format());
    }

    private Object list() throws Exception {
        var decks = new ArrayList<Map<String, Object>>();
        var problems = new ArrayList<String>();
        try (var files = Files.list(directory)) {
            for (Path file : files.filter(path -> path.getFileName().toString().endsWith(".json")).toList()) {
                try {
                    String id = file.getFileName().toString().replace(".json", "");
                    byte[] bytes = removedDecks.readSaved(id);
                    var stored = JSON.fromJson(new String(bytes, StandardCharsets.UTF_8), StoredDeck.class);
                    if (stored.version() != 1) { throw new IllegalArgumentException("Unsupported version"); }
                    decks.add(summary(id, stored, DeckLibraryTrash.token(bytes)));
                } catch (Exception error) { problems.add(file.getFileName() + ": " + error.getMessage()); }
            }
        }
        decks.sort(Comparator.comparingLong((Map<String, Object> deck) -> (long) deck.get("updated")).reversed());
        return Map.of("decks", decks, "problems", problems);
    }

    private static Map<String, Object> summary(String id, StoredDeck stored, String libraryToken) {
        if (stored == null || stored.version() != 1) throw new IllegalArgumentException("Unsupported deck file version");
        int main = stored.cards().stream().filter(card -> card.section().equals("Main")).mapToInt(DeckEditor.Edit::quantity).sum();
        return Map.of("id", id, "name", stored.name(), "format", stored.format(), "count", main,
                "updated", stored.updated(), "libraryToken", libraryToken);
    }

    private Object save() {
        try {
            workshop.assertWritable(deckId);
            var snapshot = editor.snapshot();
            var stored = new StoredDeck(1, snapshot.name(), format, snapshot.entries().stream()
                    .map(entry -> new DeckEditor.Edit(entry.section(), entry.card().id(), entry.quantity())).toList(),
                    System.currentTimeMillis(), editor.toDeck().getTokenPrintings());
            Path target = file(deckId);
            Path temporary = directory.resolve(deckId + ".tmp");
            Files.writeString(temporary, JSON.toJson(stored), StandardCharsets.UTF_8);
            try { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE); }
            catch (AtomicMoveNotSupportedException error) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
            workshop.ensureVersion(deckId, snapshot, format);
            saveError = null;
        } catch (Exception error) { saveError = error.getMessage(); }
        return state();
    }

    private Object state() {
        requireDeck();
        var result = new LinkedHashMap<String, Object>();
        result.put("id", deckId);
        try { result.put("libraryToken", DeckLibraryTrash.token(removedDecks.readSaved(deckId))); }
        catch (java.io.IOException error) { result.put("libraryToken", ""); }
        result.put("format", format);
        result.put("deck", editor.snapshot());
        result.put("validation", editor.validate(DeckFormat.valueOf(format)));
        result.put("commanderChoices", DeckCommanders.choices(editor.toDeck()));
        result.put("saveError", saveError);
        result.put("tokenPreviews", DeckTokens.view(deckId, editor).tokens().stream().map(DeckTokens.Token::card).toList());
        return result;
    }

    private String export(String kind) {
        requireDeck();
        var result = new StringBuilder();
        if (kind.equals("forge")) {
            result.append("[metadata]\nName=").append(editor.snapshot().name().replaceAll("[\\r\\n]", " ")).append("\n");
            for (var section : editor.toDeck()) {
                if (!section.getValue().isEmpty()) {
                    result.append('[').append(section.getKey().name()).append("]\n")
                            .append(section.getValue().toCardList("\n")).append('\n');
                }
            }
        } else {
            for (DeckSection section : DeckSection.values()) {
                var entries = editor.snapshot().entries().stream().filter(entry -> entry.section().equals(section.name())).toList();
                if (entries.isEmpty()) { continue; }
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
        }
        return result.toString();
    }

    private Object practice(String action, int index) {
        requireDeck();
        switch (action) {
            case "shuffle", "mulligan" -> {
                if (action.equals("shuffle")) { mulligans = 0; } else { mulligans++; }
                library.clear(); hand.clear(); draws = 0;
                for (var entry : editor.snapshot().entries()) {
                    if (entry.section().equals("Main")) {
                        for (int i = 0; i < entry.quantity(); i++) { library.add(entry.card()); }
                    }
                }
                if (library.size() < 7) { throw new IllegalArgumentException("Add at least 7 main-deck cards to practice"); }
                Collections.shuffle(library);
                for (int i = 0; i < 7; i++) { hand.add(library.remove(library.size() - 1)); }
            }
            case "draw" -> {
                if (!library.isEmpty()) { hand.add(library.remove(library.size() - 1)); draws++; }
            }
            case "bottom" -> {
                if (index < 0 || index >= hand.size()) { throw new IllegalArgumentException("Choose a card from your hand"); }
                library.add(0, hand.remove(index));
            }
            default -> throw new IllegalArgumentException("Unknown practice action");
        }
        return Map.of("hand", List.copyOf(hand), "remaining", library.size(), "mulligans", mulligans, "draws", draws);
    }

    private SyncLobby multiplayer() {
        if (multiplayer == null) {
            if (resources == null) { throw new IllegalStateException("Engine resources are still loading"); }
            multiplayer = new SyncLobby(resources, directory.getParent());
        }
        return multiplayer;
    }
    private void clearPractice() { library.clear(); hand.clear(); mulligans = 0; draws = 0; handTestId = null; }
    private void requireWorkshopDeck(JsonObject p) {
        requireDeck();
        workshop.assertWritable(deckId);
        if (!deckId.equals(string(p, "deckId", "")) || !p.has("revision") || p.get("revision").getAsLong() != editor.snapshot().revision())
            throw new IllegalArgumentException("The deck changed. Refresh the workshop before continuing.");
    }
    private static List<String> opponentIds(JsonObject p) {
        var opponents = new ArrayList<String>();
        if (p.has("opponents")) {
            if (!p.get("opponents").isJsonArray()) throw new IllegalArgumentException("Opponents must be a list of deck IDs");
            for (var opponent : p.getAsJsonArray("opponents")) {
                if (!opponent.isJsonPrimitive() || !opponent.getAsJsonPrimitive().isString()) throw new IllegalArgumentException("Each opponent must be a deck ID");
                opponents.add(opponent.getAsString());
            }
        } else opponents.add(string(p, "opponent", "green"));
        return opponents;
    }
    private void startMatch(JsonObject p, List<String> opponents, String testId) throws Exception {
        validateMatchStart(p);
        var prepared = preparedMatch(p);
        if (testId == null) playtests.interruptActive();
        if (match != null) match.close();
        var session = MatchSession.solo(prepared.deck(), format, opponents, resources, directory.getParent());
        try {
            if (testId != null) playtests.attach(testId, session);
            match = SeatConnection.local(session);
        } catch (RuntimeException error) { session.closeTable(); throw error; }
    }
    private void validateMatchStart(JsonObject p) {
        if (match != null && !match.finished()) throw new IllegalStateException("Finish or concede the current match first");
        if (p.has("deckId") && !deckId.equals(string(p, "deckId", "")) || p.has("revision") && p.get("revision").getAsLong() != editor.snapshot().revision())
            throw new IllegalArgumentException("The deck changed. Reopen match setup before starting.");
        var prepared = preparedMatch(p);
        if (prepared.preview().problem() != null) throw new IllegalArgumentException(prepared.preview().problem());
        if (multiplayer != null && multiplayer.connected()) throw new IllegalStateException("Leave the current table first");
    }
    private MatchSetup.Prepared preparedMatch(JsonObject p) {
        var prepared = MatchSetup.prepare(editor.toDeck(), format, string(p, "commanderId", ""));
        var preview = prepared.preview();
        if (preview.problem() != null) return prepared;
        String problem = new DeckEditor(catalog, prepared.deck()).validate(DeckFormat.valueOf(format)).problem();
        return new MatchSetup.Prepared(prepared.deck(), new MatchSetup.Preview(preview.format(), preview.startingLife(),
                preview.commanderId(), preview.commanders(), preview.needsCommander(), preview.commanderChoices(), problem));
    }
    private void requireDeck() {
        if (editor == null) throw new IllegalStateException("Open or create a deck first");
        workshop.assertNotRemoved(deckId);
    }
    private void ensureSaved() {
        if (deckId != null) workshop.assertWritable(deckId);
        if (saveError != null) { throw new IllegalStateException("Save the current deck before switching: " + saveError); }
    }
    private Path file(String id) {
        if (!UUID.fromString(id).toString().equals(id)) { throw new IllegalArgumentException("Invalid deck ID"); }
        return directory.resolve(id + ".json");
    }
    private static String checkedFormat(String value) {
        if (!List.of("Constructed", "Commander", "Limited").contains(value)) {
            throw new IllegalArgumentException("Unsupported format");
        }
        return value;
    }
    private static String checkedName(String value) {
        if (value == null || value.isBlank() || value.length() > 100) {
            throw new IllegalArgumentException("Deck name must be 1..100 characters");
        }
        return value.strip();
    }
    private static String string(JsonObject value, String key, String fallback) {
        return value.has(key) && !value.get(key).isJsonNull() ? value.get(key).getAsString() : fallback;
    }
    private static Integer integer(JsonObject value, String key) {
        return value.has(key) && !value.get(key).isJsonNull() ? value.get(key).getAsInt() : null;
    }
    private static int number(JsonObject value, String key, int fallback) {
        Integer result = integer(value, key); return result == null ? fallback : result;
    }
}
