package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Desktop commands use private child-process pipes; multiplayer uses versioned JSON synchronization. */
public final class DesktopEngine implements AutoCloseable {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private final CardCatalog catalog;
    private final Path directory;
    private final DeckDocumentSession documents;
    private final DeckPlaytests playtests;
    private Path resources;
    private SeatConnection match;
    private MatchSession localMatch;
    private boolean automated;
    private MatchTable simulation;
    private SyncLobby multiplayer;
    private final Map<String, SyncLobby> localSeats = new LinkedHashMap<>();

    public DesktopEngine(CardCatalog catalog, Path directory) throws Exception {
        this.catalog = catalog;
        this.directory = directory.toAbsolutePath().normalize();
        Files.createDirectories(this.directory);
        playtests = new DeckPlaytests(this.directory);
        documents = new DeckDocumentSession(catalog, this.directory, playtests);
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 2) { throw new IllegalArgumentException("Expected resource and deck directories"); }
        PrintStream protocol = System.out;
        System.setOut(System.err); // Forge diagnostics must never corrupt the JSON protocol.
        protocol.println(JSON.toJson(Map.of("event", "loading", "message", "Loading Forge card library…")));
        var data = EngineResources.load(Path.of(args[0]));
        var engine = new DesktopEngine(CardCatalog.fromDatabases(data.getAvailableDatabases().values()), Path.of(args[1]));
        engine.resources = Path.of(args[0]);
        engine.documents.loadAnalysisFormats(engine.resources);
        protocol.println(JSON.toJson(Map.of("event", "ready", "printings", engine.catalog.size(), "protocolVersion", 2, "capabilities", List.of("seat-sync", "snapshots", "command-receipts", "resume", "table-social", "deck-workshop", "deck-analysis", "source-actions", "deck-library", "deck-tokens", "semantic-priority-actions", "explicit-deck-setup", "decision-tempo", "automated-seats"))));
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
        if (simulation != null) simulation.close();
        if (multiplayer != null) {
            multiplayer.close();
        }
    }

    public Object dispatch(String method, JsonObject arguments) throws Exception {
        JsonObject p = arguments == null ? new JsonObject() : arguments;
        if (simulation != null && List.of("matchStart", "workshopStartTest", "multiplayerHost", "multiplayerJoin").contains(method))
            throw new IllegalStateException("Stop the automated simulation before opening another table");
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
                case "multiplayerSelectDeck" -> { var loaded = documents.loadLobbyDeck(string(p, "deckId", "")); yield seat.selectDeck(loaded.deck(), loaded.format()); }
                case "multiplayerReady" -> seat.ready(p);
                case "multiplayerTempo" -> seat.configureTempo(p);
                case "multiplayerReturn" -> seat.returnToLobby();
                case "multiplayerClose" -> seat.close();
                case "matchState" -> seat.match().state();
                case "matchAction" -> seat.match().action(p);
                case "matchSourceActions" -> seat.match().sourceActions(p);
                case "matchConcede" -> seat.match().concede(p);
                case "matchTempoPresented" -> seat.match().tempoPresented(p);
                default -> throw new IllegalArgumentException("Command is not available on a seat connection");
            };
        }
        return switch (method) {
            case "automatedEnable" -> {
                if (!automated && (match != null && !match.finished() || multiplayer != null && multiplayer.connected()))
                    throw new IllegalStateException("Configure automated seats before opening a table");
                automated = true;
                yield Map.of("enabled", true);
            }
            case "automatedState" -> ownedAutomatedSeats().stream().map(MatchSession::automatedObservation).toList();
            case "automatedSimulationStart" -> {
                if (!automated) throw new IllegalStateException("Enable the automated host before starting a simulation");
                if (match != null && !match.finished() || multiplayer != null && multiplayer.connected())
                    throw new IllegalStateException("Finish the current match and leave the lobby before simulating");
                var requested = p.getAsJsonArray("decks");
                if (requested == null || requested.size() != 4) throw new IllegalArgumentException("Choose four decks for this Commander simulation");
                var players = new ArrayList<forge.game.player.RegisteredPlayer>();
                for (var entry : requested) {
                    var assigned = assignedOpponent(entry.getAsString(), "Commander");
                    players.add(MatchTable.automated(assigned.deck(), "Commander", assigned.name()));
                }
                if (simulation != null) simulation.close();
                simulation = new MatchTable("Commander", players, resources, directory.getParent(), true);
                simulation.start();
                yield simulation.spectatorState();
            }
            case "automatedSimulationState" -> simulation == null ? null : simulation.spectatorState();
            case "automatedSimulationStop" -> {
                if (simulation == null || !simulation.id.equals(string(p, "sessionId", ""))) throw new IllegalArgumentException("No such simulation");
                simulation.close(); simulation = null;
                yield Map.of("stopped", true);
            }
            case "automatedAction" -> ownedAutomatedSeat(p).action(p);
            case "automatedFault" -> {
                var owner = ownedAutomatedSeat(p);
                String reason = string(p, "reasonCode", "unavailable");
                if (!reason.matches("[A-Za-z0-9-]{1,96}")) throw new IllegalArgumentException("Invalid automated diagnostic code");
                System.err.println("Mana AI stopped: game=" + string(p, "sessionId", "") + " seat=" + number(p, "seatId", -1) + " reason=" + reason);
                owner.automationFault();
                yield Map.of("stopped", true);
            }
            case "deckAnalysis" -> documents.analyze(scope(p));
            case "analysisProfile" -> documents.saveAnalysisProfile(scope(p), metadataRevision(p), string(p, "fingerprint", ""), p.getAsJsonObject("profile"));
            case "analysisReview" -> documents.reviewAnalysis(scope(p), metadataRevision(p), string(p, "fingerprint", ""),
                    string(p, "recommendationId", ""), string(p, "decision", ""), string(p, "note", ""));
            case "analysisConsider" -> documents.considerAnalysis(scope(p), metadataRevision(p), string(p, "fingerprint", ""), string(p, "recommendationId", ""), string(p, "note", ""));
            case "testMatchViews" -> multiplayer().testViews();
            case "multiplayerLocalJoin" -> { var id = UUID.randomUUID().toString(); localSeats.put(id, multiplayer().localGuest()); yield Map.of("connectionId", id); }
            case "search" -> catalog.browse(new CardCatalog.Query(string(p, "text", ""),
                    integer(p, "colors"), integer(p, "maxManaValue"), number(p, "offset", 0), number(p, "limit", 48)),
                    string(p, "type", ""), string(p, "sort", "name"), !p.has("unique") || p.get("unique").getAsBoolean(),
                    integer(p, "colorIdentity"), string(p, "role", ""));
            case "cardPrintings" -> catalog.printings(string(p, "cardId", ""), number(p, "offset", 0), number(p, "limit", 6),
                    string(p, "text", ""), string(p, "finish", ""));
            case "deckTokens" -> documents.tokens(scope(p));
            case "tokenPrintings" -> documents.tokenPrintings(scope(p), string(p, "tokenId", ""), number(p, "offset", 0), number(p, "limit", 4));
            case "setTokenPrinting" -> documents.setTokenPrinting(scope(p), string(p, "tokenId", ""), string(p, "printingId", null));
            case "replacePrinting" -> documents.replacePrinting(scope(p), string(p, "section", ""), string(p, "cardId", ""), string(p, "printingId", ""));
            case "deckInsights" -> documents.insights();
            case "workshop" -> documents.workshop(scope(p));
            case "workshopMetadata" -> documents.updateWorkshop(scope(p), p);
            case "workshopRestore" -> documents.restoreVersion(scope(p), string(p, "versionId", ""));
            case "workshopMove" -> documents.moveWorkshopCard(scope(p), p.get("metadataRevision").getAsLong(), string(p, "cardId", ""), string(p, "to", ""), string(p, "cutCardId", ""));
            case "workshopSourcePreview" -> documents.previewSource(string(p, "url", ""));
            case "workshopSourceRefresh" -> documents.refreshSource(scope(p));
            case "workshopSourceApply" -> documents.applySource(sourceScope(p), string(p, "token", ""), string(p, "choice", "remote"));
            case "workshopTests" -> playtests.list(string(p, "deckId", ""));
            case "workshopInspectTest" -> documents.inspectTest(scope(p), string(p, "testId", ""));
            case "workshopInspectVersion" -> documents.inspectVersion(scope(p), string(p, "versionId", ""));
            case "workshopTestNote" -> playtests.note(string(p, "deckId", ""), string(p, "testId", ""), string(p, "note", ""));
            case "workshopPractice" -> documents.practiceHand(scope(p), string(p, "action", "new"), integer(p, "index"), string(p, "question", ""));
            case "workshopStartTest" -> {
                var prepared = documents.prepareTest(scope(p), string(p, "commanderId", ""));
                validateMatchStart(prepared);
                var opponents = opponentIds(p);
                var record = documents.beginGameTest(prepared, opponents, string(p, "question", ""));
                try { startMatch(prepared, opponents, record.id()); }
                catch (Exception error) { playtests.finish(record.id(), "error", error.getMessage()); throw error; }
                yield playtests.get(prepared.scope().deckId(), record.id());
            }
            case "list" -> documents.list();
            case "deletedDecks" -> documents.deletedDecks();
            case "deleteDeck" -> { requireIdleDeckLibrary(); yield documents.delete(string(p, "deckId", ""), string(p, "libraryToken", "")); }
            case "restoreDeck" -> { requireIdleDeckLibrary(); yield documents.restore(string(p, "deckId", ""), string(p, "restoreToken", "")); }
            case "new" -> documents.create(string(p, "name", "Untitled deck"), string(p, "format", "Constructed"));
            case "open" -> documents.open(string(p, "id", ""));
            case "duplicate" -> documents.duplicate(scope(p), string(p, "name", ""));
            case "snapshot" -> documents.state();
            case "setCommanders" -> documents.setCommanders(scope(p), List.of(JSON.fromJson(p.get("cardIds"), String[].class)));
            case "edit" -> documents.edit(scope(p), List.of(JSON.fromJson(p.get("edits"), DeckEditor.Edit[].class)));
            case "rename" -> documents.rename(scope(p), string(p, "name", ""));
            case "undo" -> documents.undo(scope(p));
            case "redo" -> documents.redo(scope(p));
            case "format" -> documents.changeFormat(scope(p), string(p, "format", "Constructed"));
            case "save" -> documents.save(scope(p));
            case "deckPresets" -> DeckPresets.list();
            case "presetImport" -> documents.importPreset(string(p, "id", ""));
            case "importPreview" -> documents.importPreview(string(p, "text", ""));
            case "import" -> documents.importDeck(string(p, "name", "Imported deck"), string(p, "text", ""), string(p, "format", "Auto"));
            case "export" -> documents.export(scope(p), string(p, "kind", "text"));
            case "practice" -> documents.practice(string(p, "action", "shuffle"), number(p, "index", -1));
            case "multiplayerHost" -> multiplayer().host(string(p, "format", "Constructed"), number(p, "playerCount", 2),
                    p.has("autoPortForward") && p.get("autoPortForward").getAsBoolean(), string(p, "transport", "mixed"));
            case "multiplayerJoin" -> multiplayer().join(string(p, "address", ""));
            case "multiplayerConfigure" -> multiplayer().configure(string(p, "tableId", ""), string(p, "format", "Constructed"), number(p, "playerCount", 2));
            case "multiplayerSetSeat" -> multiplayer().setSeat(p);
            case "multiplayerSelectDeck" -> {
                var loaded = documents.loadLobbyDeck(string(p, "deckId", ""));
                yield multiplayer().selectDeck(loaded.deck(), loaded.format());
            }
            case "multiplayerReady" -> multiplayer().ready(p);
            case "multiplayerTempo" -> multiplayer().configureTempo(p);
            case "multiplayerStart" -> multiplayer().start();
            case "multiplayerState" -> multiplayer().state();
            case "tableSocial" -> multiplayer().social(p);
            case "tableChat" -> multiplayer().chat(p);
            case "tableName" -> multiplayer().rename(p);
            case "multiplayerReconnect" -> multiplayer().reconnect();
            case "multiplayerReturn" -> multiplayer().returnToLobby();
            case "multiplayerClose" -> multiplayer().close();
            case "matchOpponents" -> documents.opponents();
            case "matchSetup" -> documents.setupView(documents.prepareSetup(string(p, "deckId", null), string(p, "commanderId", "")));
            case "matchStart" -> {
                var prepared = documents.prepareStart(scope(p), string(p, "commanderId", ""));
                startMatch(prepared, opponentIds(p), null);
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
            case "matchTempoPresented" -> {
                if (multiplayer != null && multiplayer.connected()) yield multiplayer.match().tempoPresented(p);
                if (match == null) throw new IllegalStateException("No active match");
                yield match.tempoPresented(p);
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

    private void requireIdleDeckLibrary() {
        if (simulation != null) throw new IllegalStateException("Stop the automated simulation before deleting or restoring decks");
        if (multiplayer != null && multiplayer.connected() || localSeats.values().stream().anyMatch(SyncLobby::connected))
            throw new IllegalStateException("Leave the current lobby before deleting or restoring a saved deck.");
        if (match != null && !match.finished())
            throw new IllegalStateException("Finish or concede the current game before deleting or restoring a saved deck.");
    }

    private SyncLobby multiplayer() {
        if (multiplayer == null) {
            if (resources == null) { throw new IllegalStateException("Engine resources are still loading"); }
            multiplayer = new SyncLobby(resources, directory.getParent(), automated);
        }
        return multiplayer;
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
    private void startMatch(DeckDocumentSession.PreparedDeck prepared, List<String> opponents, String testId) throws Exception {
        validateMatchStart(prepared);
        if (testId == null) playtests.interruptActive();
        if (match != null) match.close();
        var assigned = new ArrayList<MatchSession.OpponentDeck>();
        for (String opponent : opponents) assigned.add(assignedOpponent(opponent, prepared.format()));
        var session = MatchSession.soloDecks(prepared.deck(), prepared.format(), assigned, resources, directory.getParent(), automated);
        try {
            if (testId != null) playtests.attach(testId, session);
            match = SeatConnection.local(session);
            localMatch = session;
        } catch (RuntimeException error) { session.closeTable(); throw error; }
    }
    private List<MatchSession> ownedAutomatedSeats() {
        if (!automated) return List.of();
        if (simulation != null) return simulation.automatedSeats();
        if (multiplayer != null && multiplayer.connected()) return multiplayer.automatedSeats();
        return localMatch == null ? List.of() : localMatch.automatedSeats();
    }
    private MatchSession.OpponentDeck assignedOpponent(String opponent, String format) throws Exception {
        if (!opponent.startsWith("deck:")) {
            MatchSession.validateOpponents(format, List.of(opponent));
            var deck = MatchSession.aiDeck(opponent, format);
            return new MatchSession.OpponentDeck(deck, deck.getName());
        }
        var saved = documents.loadLobbyDeck(opponent.substring(5));
        if (!saved.format().equals(format)) throw new IllegalArgumentException("AI decks must use this table's format");
        var preview = MatchSetup.prepare(saved.deck(), saved.format(), "");
        if (preview.preview().problem() != null) throw new IllegalArgumentException(preview.preview().problem());
        return new MatchSession.OpponentDeck(preview.deck(), saved.deck().getName());
    }
    private MatchSession ownedAutomatedSeat(JsonObject p) {
        int playerId = number(p, "seatId", -1);
        String gameId = string(p, "sessionId", "");
        return ownedAutomatedSeats().stream().filter(session -> {
            var state = session.state();
            return gameId.equals(state.get("id")) && Integer.valueOf(playerId).equals(state.get("viewerId"));
        }).findFirst().orElseThrow(() -> new IllegalArgumentException("No owned automated seat in that game"));
    }
    private void validateMatchStart(DeckDocumentSession.PreparedDeck prepared) {
        if (match != null && !match.finished()) throw new IllegalStateException("Finish or concede the current match first");
        documents.requireCurrent(prepared);
        if (prepared.preview().problem() != null) throw new IllegalArgumentException(prepared.preview().problem());
        if (multiplayer != null && multiplayer.connected()) throw new IllegalStateException("Leave the current table first");
    }
    private static DeckDocumentSession.RequestScope scope(JsonObject p) {
        return new DeckDocumentSession.RequestScope(p.has("deckId") ? string(p, "deckId", "") : null,
                p.has("revision") ? p.get("revision").getAsLong() : null);
    }
    private static DeckDocumentSession.RequestScope sourceScope(JsonObject p) {
        return string(p, "deckId", null) == null ? new DeckDocumentSession.RequestScope(null, null) : scope(p);
    }
    private static long metadataRevision(JsonObject p) {
        if (!p.has("metadataRevision")) throw new IllegalArgumentException("Missing analysis notes revision");
        return p.get("metadataRevision").getAsLong();
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
