package forge.api;

import com.google.gson.*;
import forge.deck.*;
import forge.game.player.RegisteredPlayer;
import forge.gamemodes.net.server.FServerManager;
import forge.api.sync.SyncTransport;
import forge.api.sync.TableConversation;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.function.Supplier;

/** Lobby orchestration and a connection to a seat. No gameplay projection lives here. */
final class SyncLobby {
    private final Path resources, profile;
    private final CardCatalog catalog;
    private final AiDeckCatalog aiDecks;
    private final Supplier<CompletableFuture<Map<String, String>>> discoverLocalAddresses;
    private final Supplier<CompletableFuture<String>> discoverExternalAddress;
    private final Supplier<EncounterProgressStore> encounterStoreFactory;
    private SyncTransport.Server server;
    private SyncTransport.Connection connection;
    private final List<Slot> slots = new ArrayList<>();
    private MatchTable table;
    private long transportEpoch;
    private final boolean automated;
    private TableConversation conversation = new TableConversation();
    private String format = "Constructed";
    private long revision;
    private DecisionTempo.Settings tempo = DecisionTempo.Settings.off();
    private int port;
    private PortForwarding forwarding;
    private CompletableFuture<Map<String, String>> localAddresses = CompletableFuture.completedFuture(Map.of());
    private CompletableFuture<String> external = CompletableFuture.completedFuture(null);
    private MatchSetupSpecification matchSetup;
    private String matchSetupConsent;
    private EncounterProgressStore progressStore;
    private String partyId, encounterId;
    private EncounterProgressStore.Progress savedProgress;
    private EncounterProgressStore.Receipt pendingReceipt;
    private String saveState = "ready", saveError;
    private boolean retirementReady;
    private static final class Slot {
        String participantId = UUID.randomUUID().toString();
        String name; LobbyDeckSelection selection; boolean ready, occupied, ai;
        String readyConsent;
        String opponentId;
        Slot(String name, boolean occupied) { this.name = name; this.occupied = occupied; }
    }
    private record SlotState(String name, LobbyDeckSelection selection, boolean ready, boolean occupied, boolean ai, String opponentId, String participantId, String readyConsent) { }
    private record LobbyState(String format, DecisionTempo.Settings tempo, MatchTable table, MatchSetupSpecification matchSetup, String consent, List<SlotState> slots) { }
    private LobbyState revisionState() {
        return new LobbyState(format, tempo, table, matchSetup, matchSetupConsent, slots.stream().map(s ->
                new SlotState(s.name, s.selection, s.ready, s.occupied, s.ai, s.opponentId, s.participantId, s.readyConsent)).toList());
    }
    private void recordMutation(LobbyState before) { if (!before.equals(revisionState())) revision++; }
    SyncLobby(Path resources, Path profile) {
        this(resources, profile, false);
    }
    SyncLobby(Path resources, Path profile, boolean automated) {
        this(resources, profile, automated, null);
    }
    SyncLobby(Path resources, Path profile, boolean automated, AiDeckCatalog aiDecks) {
        this(resources, profile, automated, aiDecks,
                () -> CompletableFuture.supplyAsync(FServerManager::getAllLocalAddresses),
                () -> CompletableFuture.supplyAsync(FServerManager::getExternalAddress));
    }
    SyncLobby(Path resources, Path profile, boolean automated, AiDeckCatalog aiDecks,
              Supplier<CompletableFuture<Map<String, String>>> discoverLocalAddresses,
              Supplier<CompletableFuture<String>> discoverExternalAddress) {
        this(resources, profile, automated, aiDecks, discoverLocalAddresses, discoverExternalAddress,
                () -> new EncounterProgressStore(profile));
    }
    SyncLobby(Path resources, Path profile, boolean automated, AiDeckCatalog aiDecks,
              Supplier<CompletableFuture<Map<String, String>>> discoverLocalAddresses,
              Supplier<CompletableFuture<String>> discoverExternalAddress, Supplier<EncounterProgressStore> encounterStoreFactory) {
        this.automated = automated;
        this.discoverLocalAddresses = Objects.requireNonNull(discoverLocalAddresses);
        this.discoverExternalAddress = Objects.requireNonNull(discoverExternalAddress);
        this.encounterStoreFactory = Objects.requireNonNull(encounterStoreFactory);
        this.resources = resources; this.profile = profile;
        catalog = CardCatalog.fromDatabases(forge.StaticData.instance().getAvailableDatabases().values());
        this.aiDecks = aiDecks == null ? new AiDeckCatalog(catalog, null) : aiDecks;
        new DeckAnalysisFormats(catalog).load(resources);
    }
    Object host(String format, int count, boolean autoForward) {
        return host(format, count, autoForward, "mixed");
    }
    Object host(String format, int count, boolean autoForward, String transport) {
        close(); HeadlessPlatform.initialize(resources, profile);
        // Recovery and all later journal writes run on the store's I/O worker.
        progressStore = encounterStoreFactory.get();
        synchronized (this) {
            tempo = DecisionTempo.Settings.off();
            configureSeats(format, count); slots.get(0).occupied = true;
            conversation = new TableConversation(); conversation.join(0, slots.get(0).name);
            revision = 1;
            long epoch = ++transportEpoch;
            server = new SyncTransport.Server(this::dispatch, this::joinSeat, seat -> disconnected(epoch, seat), Set.of("match-history", "match-setup")); connection = server.local(0);
            try { port = transport.equals("local") ? 0 : server.listen(); } catch (java.io.IOException e) { close(); throw new IllegalStateException("Could not host the table", e); }
            if (port != 0) {
                // Endpoint metadata is discovered once, off the command pipe.
                // A lobby/game poll must never enumerate operating-system NICs.
                localAddresses = discovery(discoverLocalAddresses, Map.<String, String>of())
                        .thenApply(SyncLobby::copyLocalAddresses).exceptionally(ignored -> Map.of());
                external = discovery(discoverExternalAddress, null);
            }
            forwarding = new PortForwarding(port, port != 0 && autoForward);
        }
        // Connect outside the lobby monitor: the accept worker needs it for dispatch.
        if (transport.equals("network")) try { connection = server.remoteHost(); } catch (java.io.IOException e) { close(); throw new IllegalStateException(e); }
        return state();
    }
    SyncLobby localGuest() {
        if (server == null) throw new IllegalStateException("Host a table first");
        var guest = new SyncLobby(resources, profile, automated); guest.connection = server.joinLocal(); return guest;
    }
    Object join(String address) {
        // Validate and connect before releasing a working seat. A typo must not
        // turn an attempted join into a leave or concession at the current table.
        var next = new SyncTransport.Remote(NetworkInvite.address(address));
        close(); connection = next; return state();
    }
    private synchronized int joinSeat() {
        if (table != null) throw new IllegalStateException("This table has already started");
        for (int i = 1; i < slots.size(); i++) if (!slots.get(i).occupied && !slots.get(i).ai) {
            slots.get(i).participantId = UUID.randomUUID().toString();
            slots.get(i).occupied = true; slots.get(i).name = "Player " + (i + 1);
            renewSetupConsent();
            revision++;
            conversation.join(i, slots.get(i).name); return i;
        }
        throw new IllegalStateException("The table is full");
    }
    private Object request(String method, JsonObject args) {
        var current = connection;
        if (current == null) throw new IllegalStateException("Join or host a lobby first.");
        return current.request(method, args);
    }
    Object configure(String tableId, String format, int count) { return request("configure", object(Map.of("tableId", tableId, "format", format, "count", count))); }
    Object configureMatch(JsonObject args) { return request("configureMatch", args); }
    Object retryEncounterSave(JsonObject args) { return request("retryEncounterSave", args); }
    Object setSeat(JsonObject args) { return request("seat", args); }
    Object configureTempo(JsonObject args) { return request("tempo", args); }
    Object selectDeck(Deck deck, String format) {
        return selectDeck(deck, format, null);
    }
    Object selectDeck(Deck deck, String format, String tableId) {
        var entries = new ArrayList<Map<String, Object>>();
        for (var section : deck) for (var card : section.getValue()) entries.add(Map.of("section", section.getKey().name(), "cardId", CardCatalog.describe(card.getKey()).id(), "quantity", card.getValue()));
        var args = object(Map.of("format", format, "name", deck.getName(), "cards", entries, "tokenPrintings", deck.getTokenPrintings()));
        if (tableId != null) args.addProperty("tableId", tableId);
        return request("deck", args);
    }
    Object clearDeck() { return clearDeck(null); }
    Object clearDeck(String tableId) {
        var args = object(Map.of("clear", true)); if (tableId != null) args.addProperty("tableId", tableId);
        return request("deck", args);
    }
    Object ready(boolean value) { return request("ready", object(Map.of("ready", value))); }
    Object ready(JsonObject args) { return request("ready", args); }
    Object social(JsonObject args) { return request("social", args); }
    Object chat(JsonObject args) { return request("chat", args); }
    Object rename(JsonObject args) { return request("name", args); }
    Object start() { return request("start", new JsonObject()); }
    Object returnToLobby() { return request("return", new JsonObject()); }
    Object state() { return connection == null ? idle() : request("lobby", new JsonObject()); }
    Object reconnect() { if (connection instanceof SyncTransport.Remote remote) remote.reconnect(); return state(); }
    MatchEndpoint match() { return new SeatConnection(connection); }
    boolean connected() { return connection != null; }
    synchronized List<MatchSession> automatedSeats() { return server == null || table == null ? List.of() : table.automatedSeats(); }
    synchronized Object testViews() {
        if (!Boolean.getBoolean("mana.test") || server == null || table == null) throw new IllegalStateException("Seat audit is available only to an isolated test host");
        return table.testViews();
    }
    boolean hasMatch() { return connection != null && Boolean.TRUE.equals(((Map<?, ?>)state()).get("matchActive")); }
    Object close() {
        MatchTable owner;
        synchronized (this) {
            if (table != null && table.encounterId() != null && table.game.isGameOver() && !retirementReady)
                throw new IllegalStateException(saveState.equals("failed") ? "Retry saving the encounter before leaving this table." : "Encounter progress is still being saved.");
            owner = table;
        }
        // Stop an active game or verify its durable terminal barrier before
        // disconnecting anyone. No disk I/O occurs in this rules boundary.
        if (owner != null) owner.close();
        synchronized (this) { if (table == owner) table = null; }
        synchronized (this) { transportEpoch++; }
        var current = connection; connection = null;
        if (current != null) { if (server == null) try { current.request("leave", new JsonObject()); } catch (Exception ignored) { } current.close(); }
        if (server != null) { server.close(); server = null; }
        if (forwarding != null) { forwarding.close(); forwarding = null; }
        var store = progressStore; progressStore = null; if (store != null) store.close();
        synchronized (this) {
            slots.clear(); port = 0;
            matchSetup = null; matchSetupConsent = null; partyId = null; encounterId = null; savedProgress = null; pendingReceipt = null;
            saveState = "ready"; saveError = null; retirementReady = false;
            localAddresses = CompletableFuture.completedFuture(Map.of());
            external = CompletableFuture.completedFuture(null);
        }
        return idle();
    }

    private void disconnected(long epoch, int seat) {
        MatchTable owner;
        synchronized (this) { owner = epoch == transportEpoch ? table : null; }
        // Do not hold the lobby monitor while entering the rules state boundary.
        // An old transport epoch cannot revoke a newly hosted table's consent.
        if (owner != null) owner.seat(seat).revokeFiniteYieldForConnection();
    }
    private synchronized Object dispatch(int seat, String method, JsonObject p) {
        if (seat < 0 || seat >= slots.size() || !slots.get(seat).occupied) throw new IllegalStateException("This seat is no longer active");
        Slot slot = slots.get(seat);
        var before = revisionState();
        switch (method) {
            case "social": requireConversation(p); return conversation.snapshot(seat);
            case "chat": requireConversation(p); conversation.send(seat, p.get("kind").getAsString(), p.get("text").getAsString()); return conversation.snapshot(seat);
            case "name": requireConversation(p); requireSetup(); slot.name = conversation.rename(seat, p.get("name").getAsString()); recordMutation(before); return conversation.snapshot(seat);
            case "lobby": return lobby(seat);
            case "observe": return table == null ? null : table.seat(seat).state();
            case "reply": requireMatch(); return table.seat(seat).action(p);
            case "sourceActions": requireMatch(); return table.seat(seat).sourceActions(p);
            case "history": requireMatch(); return table.seat(seat).history(p);
            case "concede": requireMatch(); return table.seat(seat).concede(p);
            case "tempoPresented": requireMatch(); return table.seat(seat).tempoPresented(p);
            case "tempo": {
                requireHost(seat); requireSetup(); requireConversation(p);
                var next = DecisionTempo.Settings.preset(p.get("preset").getAsString(), p.get("expiry").getAsString());
                if (tempo.preset().equals(next.preset()) && tempo.expiry().equals(next.expiry())) break;
                tempo = next;
                clearHumanReady();
                conversation.activity(seat, "changed the table tempo. Ready again to accept the new timing.");
                break;
            }
            case "configure": requireHost(seat); requireSetup(); requireConversation(p); configureSeats(p.get("format").getAsString(), p.get("count").getAsInt()); break;
            case "configureMatch": requireHost(seat); requireSetup(); requireConversation(p); configureMatchInState(p); break;
            case "retryEncounterSave": {
                requireHost(seat); requireConversation(p); requireMatch();
                if (!p.has("encounterId") || !p.get("encounterId").isJsonPrimitive() || !p.getAsJsonPrimitive("encounterId").isString()
                        || !Objects.equals(encounterId, p.get("encounterId").getAsString())) throw new IllegalStateException("This encounter has changed.");
                if (saveState.equals("failed") && pendingReceipt != null) saveReceipt(table, pendingReceipt);
                return lobby(seat);
            }
            case "seat": requireHost(seat); requireSetup(); requireConversation(p); configureSeat(seat, p); break;
            case "deck": {
                requireSetup();
                if (p.has("tableId")) requireConversation(p);
                if (p.has("clear") && p.get("clear").getAsBoolean()) {
                    slot.selection = null; slot.ready = false; slot.readyConsent = null; break;
                }
                if (!format.equals(p.get("format").getAsString())) throw new IllegalArgumentException("Choose a saved " + format + " deck for this lobby.");
                String name = p.get("name").getAsString();
                if (name.isBlank() || name.length() > 200) throw new IllegalArgumentException("Invalid deck name");
                if (p.getAsJsonArray("cards").size() > 1000) throw new IllegalArgumentException("Deck has too many entries");
                var editor = new DeckEditor(catalog, new Deck(name));
                editor.apply(0, List.of(SyncTransport.JSON.fromJson(p.get("cards"), DeckEditor.Edit[].class)));
                var deck = editor.toDeck();
                deck.setTokenPrintings(DeckTokens.readPreferences(p.get("tokenPrintings")));
                slot.selection = LobbyDeckSelection.capture(deck, format, catalog);
                slot.ready = false; slot.readyConsent = null; conversation.activity(seat, "selected " + deck.getName() + "."); break;
            }
            case "ready": {
                requireSetup(); boolean ready = p.get("ready").getAsBoolean();
                if (ready && matchSetup != null && matchSetup.cooperative()) {
                    requireConversation(p);
                    if (!p.has("matchSetupConsent") || !p.get("matchSetupConsent").isJsonPrimitive()
                            || !p.getAsJsonPrimitive("matchSetupConsent").isString()
                            || !Objects.equals(matchSetupConsent, p.get("matchSetupConsent").getAsString()))
                        throw new IllegalStateException("Review the current encounter sides before readying. Update Mana Table if encounter settings are not visible.");
                }
                if (ready && tempo.enabled()) requireConversation(p);
                if (ready && slot.selection == null) throw new IllegalStateException("Choose a deck first");
                if (ready && slot.selection.problem() != null)
                    throw new IllegalStateException(slot.selection.deck().getName() + " is not legal for " + format + ": " + slot.selection.problem());
                if (ready && tempo.enabled() && (!p.has("tempoRevision") || !p.get("tempoRevision").isJsonPrimitive()
                        || !p.getAsJsonPrimitive("tempoRevision").isString() || !tempo.revision().equals(p.get("tempoRevision").getAsString())))
                    throw new IllegalStateException("Review the current table tempo before readying. Update Mana Table if timing settings are not visible.");
                if (slot.ready != ready) conversation.activity(seat, ready ? "is ready." : "is no longer ready.");
                slot.ready = ready; slot.readyConsent = ready && matchSetup != null && matchSetup.cooperative() ? matchSetupConsent : null; break;
            }
            case "start": {
                requireHost(seat); requireSetup(); String problem = startProblem(); if (problem != null) throw new IllegalStateException(problem);
                List<RegisteredPlayer> players = slots.stream().map(s -> s.ai
                        ? automated ? MatchTable.automated(s.selection.deck(), format, s.name) : MatchTable.ai(s.selection.deck(), format, s.name)
                        : MatchTable.human(s.selection.deck(), format, s.name)).toList();
                if (matchSetup != null) for (var assignment : matchSetup.seats()) players.get(assignment.index()).setTeamNumber(assignment.teamId());
                EncounterContext context = null;
                if (matchSetup != null && matchSetup.cooperative()) {
                    var participants = new ArrayList<EncounterContext.Participant>();
                    for (var assignment : matchSetup.seats()) participants.add(new EncounterContext.Participant(slots.get(assignment.index()).participantId,
                            assignment.index(), assignment.teamId(), assignment.role(), slots.get(assignment.index()).selection.preview().selectionId()));
                    context = new EncounterContext(partyId, UUID.randomUUID().toString(), matchSetup, participants);
                }
                var owner = new MatchTable(format, players, resources, profile, tempo, () -> System.nanoTime() / 1_000_000, false, context);
                table = owner; encounterId = context == null ? null : context.encounterId(); pendingReceipt = null;
                retirementReady = context == null; saveState = "ready"; saveError = null;
                owner.start().whenComplete((ignored, error) -> encounterFinished(owner));
                conversation.activity(seat, "started the game."); break;
            }
            case "return":
                if (table != null) {
                    if (!table.game.isGameOver()) throw new IllegalStateException("The match is still active");
                    if (!retirementReady) throw new IllegalStateException(saveState.equals("failed") ? "Retry saving the encounter before returning to the lobby." : "Encounter progress is still being saved.");
                    table.close(); table = null; for (var s : slots) { s.ready = s.ai && s.selection != null; s.readyConsent = null; } conversation.activity(seat, "returned the table to the lobby.");
                }
                slot.ready = false; slot.readyConsent = null; break;
            case "leave":
                if (table != null && !table.game.isGameOver()) table.seat(seat).concede(object(Map.of("sessionId", table.id)));
                conversation.leave(seat); slot.occupied = false; slot.ready = false; slot.readyConsent = null; slot.selection = null;
                renewSetupConsent(); server.revoke(seat); break;
            default: throw new IllegalArgumentException("Unknown synchronization command");
        }
        recordMutation(before);
        return lobby(seat);
    }
    private void requireHost(int seat) { if (seat != 0) throw new IllegalStateException("Only the host can change this table"); }
    private void requireConversation(JsonObject p) {
        if (!p.has("tableId") || !p.get("tableId").isJsonPrimitive() || !p.getAsJsonPrimitive("tableId").isString()
                || !conversation.id().equals(p.get("tableId").getAsString())) throw new IllegalStateException("This table has changed. Reopen the table.");
    }
    private void requireSetup() { if (table != null) throw new IllegalStateException("Return to the lobby before changing the table"); }
    private void requireMatch() { if (table == null) throw new IllegalStateException("No active match"); }
    private void clearHumanReady() { for (var slot : slots) if (!slot.ai) { slot.ready = false; slot.readyConsent = null; } }
    private void renewSetupConsent() {
        matchSetupConsent = UUID.randomUUID().toString();
        if (matchSetup != null && matchSetup.cooperative()) clearHumanReady();
    }
    private void configureMatchInState(JsonObject p) {
        try {
            if (!p.has("expectedLobbyRevision") || !p.get("expectedLobbyRevision").isJsonPrimitive()
                    || !p.getAsJsonPrimitive("expectedLobbyRevision").isNumber()
                    || p.get("expectedLobbyRevision").getAsBigDecimal().longValueExact() != revision)
                throw new IllegalStateException("The lobby has changed. Review its current seats before configuring the encounter.");
        } catch (ArithmeticException invalid) { throw new IllegalArgumentException("Invalid lobby revision", invalid); }
        if (!format.equals("Commander")) throw new IllegalStateException("Encounter setup requires Commander decks");
        var next = MatchSetupSpecification.read(p.get("specification"));
        if (next.cooperative() && !automated) throw new IllegalStateException("This host does not support automated encounter seats");
        if (next.equals(matchSetup)) return;
        for (int i = next.seats().size(); i < slots.size(); i++) if (slots.get(i).occupied)
            throw new IllegalStateException("A connected player occupies a seat you are trying to remove");
        var opponents = new HashMap<Integer, LobbyDeckSelection>(); var opponentIds = new HashMap<Integer, String>();
        for (var assignment : next.seats()) if (assignment.role().equals("opposition")) {
            var existing = assignment.index() < slots.size() ? slots.get(assignment.index()) : null;
            if (existing != null && existing.occupied) throw new IllegalStateException("Connected players must remain on the party side");
            String opponent = existing != null && existing.ai && existing.opponentId != null ? existing.opponentId : aiDecks.choices(format).get(0).get("id");
            LobbyDeckSelection selected;
            try { selected = existing != null && existing.ai && existing.selection != null ? existing.selection
                    : LobbyDeckSelection.capture(aiDecks.load(opponent, format), format, catalog); }
            catch (Exception error) { throw new IllegalArgumentException("Choose an available legal opposition deck", error); }
            if (selected.problem() != null) throw new IllegalArgumentException("Choose a legal opposition deck");
            opponents.put(assignment.index(), selected); opponentIds.put(assignment.index(), opponent);
        }
        while (slots.size() > next.seats().size()) slots.remove(slots.size() - 1);
        while (slots.size() < next.seats().size()) slots.add(new Slot("Player " + (slots.size() + 1), false));
        for (var assignment : next.seats()) {
            var slot = slots.get(assignment.index());
            if (assignment.role().equals("opposition")) {
                boolean replacement = !slot.ai;
                if (replacement) slot.participantId = UUID.randomUUID().toString();
                slot.ai = true; slot.opponentId = opponentIds.get(assignment.index()); slot.selection = opponents.get(assignment.index());
                if (replacement || automaticAiName(slot.name, assignment.index())) slot.name = "AI opponent " + (assignment.index() + 1);
            }
            slot.ready = slot.ai && slot.selection != null;
            slot.readyConsent = null;
        }
        matchSetup = next;
        refreshAiNames();
        renewSetupConsent();
        if (next.cooperative()) {
            if (partyId == null) { partyId = UUID.randomUUID().toString(); savedProgress = progressStore.current(partyId); }
        } else { partyId = null; savedProgress = null; encounterId = null; }
        conversation.activity(0, "changed the encounter sides. Ready again to accept the new setup.");
    }
    private void encounterFinished(MatchTable owner) {
        // The game worker invokes completion outside the rules/table monitor.
        // Capture only public immutable outcome facts before scheduling disk I/O.
        if (owner.encounterId() == null) return;
        var receipt = owner.terminalReceipt();
        synchronized (this) {
            if (table != owner) return;
            if (receipt == null) {
                // An interruption/error is not a completed encounter. It can
                // retire after the worker stops, without a receipt or a count.
                retirementReady = true; revision++; return;
            }
            saveReceipt(owner, receipt);
        }
    }
    private void saveReceipt(MatchTable owner, EncounterProgressStore.Receipt receipt) {
        pendingReceipt = receipt; saveState = "pending"; saveError = null; retirementReady = false; revision++;
        progressStore.commit(receipt).whenComplete((saved, error) -> {
            synchronized (SyncLobby.this) {
                if (table != owner || pendingReceipt != receipt) return;
                if (error == null) {
                    owner.allowEncounterRetirement();
                    savedProgress = saved; pendingReceipt = null; saveState = "ready"; saveError = null; retirementReady = true;
                } else {
                    error.printStackTrace(System.err);
                    saveState = "failed"; saveError = "Could not save encounter progress. Retry before leaving this match.";
                }
                revision++;
            }
        });
    }
    private void configureSeat(int host, JsonObject p) {
        int index;
        try {
            if (!p.has("seat") || !p.get("seat").isJsonPrimitive() || !p.getAsJsonPrimitive("seat").isNumber())
                throw new IllegalArgumentException("Choose an open or AI seat");
            index = p.get("seat").getAsBigDecimal().intValueExact();
        } catch (NumberFormatException | ArithmeticException invalid) { throw new IllegalArgumentException("Choose an open or AI seat"); }
        if (index <= 0 || index >= slots.size()) throw new IllegalArgumentException("Choose an open or AI seat other than the host");
        var target = slots.get(index);
        if (target.occupied) throw new IllegalStateException("A connected player occupies this seat");
        String type = p.has("type") && p.get("type").isJsonPrimitive() && p.getAsJsonPrimitive("type").isString()
                ? p.get("type").getAsString() : "";
        if (!List.of("AI", "OPEN").contains(type)) throw new IllegalArgumentException("Choose AI or an open seat");
        if (type.equals("OPEN")) {
            if (matchSetup != null && matchSetup.seats().get(index).role().equals("opposition"))
                throw new IllegalStateException("Opposition seats must remain AI-controlled");
            if (!target.ai) return;
            target.participantId = UUID.randomUUID().toString();
            target.ai = false; target.opponentId = null; target.selection = null; target.ready = false;
            target.readyConsent = null; renewSetupConsent();
            target.name = "Player " + (index + 1);
            conversation.activity(host, "opened seat " + (index + 1) + " for a friend.");
            return;
        }
        if (p.has("opponentId") && !p.get("opponentId").isJsonNull()
                && (!p.get("opponentId").isJsonPrimitive() || !p.getAsJsonPrimitive("opponentId").isString()))
            throw new IllegalArgumentException("Choose an available AI deck");
        String opponent = p.has("opponentId") && !p.get("opponentId").isJsonNull() ? p.get("opponentId").getAsString() : "";
        if (opponent.isBlank()) opponent = aiDecks.choices(format).get(0).get("id");
        Deck deck;
        try { deck = aiDecks.load(opponent, format); }
        catch (Exception error) { throw new IllegalArgumentException(error.getMessage(), error); }
        var selection = LobbyDeckSelection.capture(deck, format, catalog);
        if (selection.problem() != null) throw new IllegalArgumentException("Choose a legal AI deck");
        boolean replacement = !target.ai;
        if (replacement) target.participantId = UUID.randomUUID().toString();
        target.ai = true; target.opponentId = opponent; target.selection = selection; target.ready = true;
        if (replacement || automaticAiName(target.name, index)) target.name = aiName(index);
        if (replacement) renewSetupConsent();
        conversation.activity(host, "set seat " + (index + 1) + " to AI with " + deck.getName() + ".");
    }
    private void configureSeats(String nextFormat, int count) {
        if (!List.of("Commander", "Constructed").contains(nextFormat)) throw new IllegalArgumentException("Unsupported format");
        if (count < 2 || count > (nextFormat.equals("Commander") ? 6 : 2)) throw new IllegalArgumentException("Invalid number of seats");
        for (int i = count; i < slots.size(); i++) if (slots.get(i).occupied) throw new IllegalStateException("A connected player occupies a seat you are trying to remove");
        while (slots.size() > count) slots.remove(slots.size() - 1);
        while (slots.size() < count) slots.add(new Slot("Player " + (slots.size() + 1), false));
        for (var slot : slots) {
            if (!format.equals(nextFormat)) {
                slot.selection = null;
                if (slot.ai) { slot.ai = false; slot.opponentId = null; slot.name = "Player " + (slots.indexOf(slot) + 1); }
            }
            slot.ready = slot.ai && slot.selection != null;
            slot.readyConsent = null;
        }
        format = nextFormat;
        matchSetup = nextFormat.equals("Commander") ? MatchSetupSpecification.freeForAll(count) : null;
        refreshAiNames();
        renewSetupConsent();
        partyId = null; encounterId = null; savedProgress = null; pendingReceipt = null;
        saveState = "ready"; saveError = null; retirementReady = false;
    }
    private String startProblem() {
        if (table != null) return "A game is already running.";
        long occupied = slots.stream().filter(s -> s.occupied || s.ai).count();
        if (occupied < slots.size()) return "Waiting for players (" + occupied + "/" + slots.size() + " seats filled). Invite more friends, add AI, or change the number of seats.";
        for (var slot : slots) {
            if (!slot.ai && slot.ready && matchSetup != null && matchSetup.cooperative() && !Objects.equals(slot.readyConsent, matchSetupConsent))
                return slot.name + " needs to review the current encounter sides and ready again.";
            if (slot.selection != null && slot.selection.problem() != null) return slot.name + " needs to fix their deck before readying.";
            if (slot.selection == null || !slot.ready) return slot.name + " needs to choose a deck and ready up.";
        }
        return null;
    }
    private static boolean automaticAiName(String name, int index) {
        return Objects.equals(name, "AI opponent " + (index + 1)) || Objects.equals(name, "AI ally " + (index + 1));
    }
    private String aiName(int index) {
        return "AI " + (matchSetup != null && matchSetup.cooperative() && matchSetup.seats().get(index).role().equals("party") ? "ally " : "opponent ") + (index + 1);
    }
    private void refreshAiNames() {
        for (int i = 0; i < slots.size(); i++) if (slots.get(i).ai && automaticAiName(slots.get(i).name, i)) slots.get(i).name = aiName(i);
    }
    private Map<String, Object> lobby(int viewer) {
        var result = idle();
        result.put("tableId", conversation.id());
        result.put("revision", revision);
        result.put("tempo", tempo.view());
        result.put("mode", viewer == 0 ? "hosting" : "joined"); result.put("status", table == null ? "Connected to table." : "Game in progress.");
        result.put("hosting", viewer == 0); result.put("format", format); result.put("playerCount", slots.size()); result.put("maxPlayers", format.equals("Commander") ? 6 : 2);
        result.put("canStart", viewer == 0 && startProblem() == null); result.put("startProblem", viewer == 0 ? startProblem() : "Only the host can start the game."); result.put("matchActive", table != null);
        result.put("aiOpponents", viewer == 0 ? aiDecks.choices(format) : List.of());
        if (format.equals("Commander")) {
            result.put("matchSetupCapabilities", MatchSetupSpecification.capabilities(automated));
            result.put("matchSetup", matchSetup);
            result.put("matchSetupConsent", matchSetupConsent);
        }
        if (partyId != null) {
            var progress = new LinkedHashMap<String, Object>();
            progress.put("partyId", partyId); progress.put("encounterId", encounterId);
            progress.put("completedEncounters", savedProgress == null ? 0 : savedProgress.completedEncounters());
            progress.put("victories", savedProgress == null ? 0 : savedProgress.victories());
            progress.put("lastReceipt", savedProgress == null ? null : savedProgress.lastReceipt());
            progress.put("saveState", saveState); progress.put("saveError", saveError);
            progress.put("retryAllowed", viewer == 0 && saveState.equals("failed") && pendingReceipt != null);
            result.put("partyProgress", progress);
        }
        var rows = new ArrayList<Map<String, Object>>();
        for (int i = 0; i < slots.size(); i++) {
            var slot = slots.get(i); var row = new LinkedHashMap<String, Object>();
            row.put("index", i); row.put("name", slot.name); row.put("type", slot.ai ? "AI" : !slot.occupied ? "OPEN" : i == viewer ? "LOCAL" : "REMOTE");
            row.put("opponentId", viewer == 0 ? slot.opponentId : null);
            row.put("ready", slot.ready); row.put("local", i == viewer); row.put("team", i);
            row.put("participantId", slot.participantId);
            if (matchSetup != null) { row.put("teamId", matchSetup.seats().get(i).teamId()); row.put("role", matchSetup.seats().get(i).role()); }
            row.put("deck", slot.selection == null ? null : slot.selection.preview().name());
            row.put("deckPreview", slot.selection == null ? null : slot.selection.preview()); rows.add(row);
        }
        result.put("slots", rows);
        if (viewer == 0) {
            result.put("addresses", addresses()); result.put("internetInvite", NetworkInvite.encode(external.getNow(null), port));
            result.put("addressLookupPending", !localAddresses.isDone() || !external.isDone()); result.put("portMapping", forwarding == null ? "disabled" : forwarding.status());
        }
        return result;
    }
    private List<Map<String, Object>> addresses() {
        var addresses = new LinkedHashMap<>(localAddresses.getNow(Map.of())); addresses.putIfAbsent("This computer", "127.0.0.1");
        String internet = external.getNow(null); if (NetworkInvite.encode(internet, port) != null) addresses.put("Internet", internet.trim());
        var result = new ArrayList<Map<String, Object>>();
        for (var address : addresses.entrySet()) result.add(Map.of("label", address.getKey(), "url", address.getValue() + ":" + port,
                "invite", Objects.requireNonNullElse(NetworkInvite.encode(address.getValue(), port), ""), "preferred", address.getKey().equals("Internet")));
        return result;
    }
    private static <T> CompletableFuture<T> discovery(Supplier<CompletableFuture<T>> start, T fallback) {
        try {
            var future = start.get();
            return future == null ? CompletableFuture.completedFuture(fallback)
                    : future.handle((value, error) -> error == null && value != null ? value : fallback);
        } catch (RuntimeException error) { return CompletableFuture.completedFuture(fallback); }
    }
    private static Map<String, String> copyLocalAddresses(Map<String, String> discovered) {
        var copied = new LinkedHashMap<String, String>();
        int inspected = 0;
        for (var entry : discovered.entrySet()) {
            if (inspected++ == 64) break;
            String label = entry.getKey(), address = entry.getValue();
            if (label == null || label.isBlank() || label.length() > 128 || label.chars().anyMatch(Character::isISOControl)
                    || address == null || address.length() > 32 || NetworkInvite.encode(address, 1) == null) continue;
            copied.put(label, address.trim());
        }
        return Collections.unmodifiableMap(copied);
    }
    private static JsonObject object(Object value) { return SyncTransport.JSON.toJsonTree(value).getAsJsonObject(); }
    private static LinkedHashMap<String, Object> idle() {
        var state = new LinkedHashMap<String, Object>();
        state.put("mode", "idle"); state.put("status", "Not connected."); state.put("error", null); state.put("canStart", false); state.put("startProblem", "Host a table first.");
        state.put("revision", 0L);
        state.put("hosting", false); state.put("format", "Constructed"); state.put("playerCount", 2); state.put("maxPlayers", 2); state.put("addressLookupPending", false);
        state.put("matchActive", false); state.put("portMapping", "disabled"); state.put("internetInvite", null); state.put("addresses", List.of()); state.put("slots", List.of()); state.put("messages", List.of()); state.put("aiOpponents", List.of()); return state;
    }
}
