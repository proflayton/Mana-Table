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

/** Lobby orchestration and a connection to a seat. No gameplay projection lives here. */
final class SyncLobby {
    private final Path resources, profile;
    private final CardCatalog catalog;
    private SyncTransport.Server server;
    private SyncTransport.Connection connection;
    private final List<Slot> slots = new ArrayList<>();
    private MatchTable table;
    private TableConversation conversation = new TableConversation();
    private String format = "Constructed";
    private int port;
    private PortForwarding forwarding;
    private CompletableFuture<String> external = CompletableFuture.completedFuture(null);
    private static final class Slot {
        String name; Deck deck; boolean ready, occupied;
        Slot(String name, boolean occupied) { this.name = name; this.occupied = occupied; }
    }
    SyncLobby(Path resources, Path profile) {
        this.resources = resources; this.profile = profile;
        catalog = CardCatalog.fromDatabases(forge.StaticData.instance().getAvailableDatabases().values());
    }
    Object host(String format, int count, boolean autoForward) {
        return host(format, count, autoForward, "mixed");
    }
    Object host(String format, int count, boolean autoForward, String transport) {
        close(); HeadlessPlatform.initialize(resources, profile);
        synchronized (this) {
            configureSeats(format, count); slots.get(0).occupied = true;
            conversation = new TableConversation(); conversation.join(0, slots.get(0).name);
            server = new SyncTransport.Server(this::dispatch, this::joinSeat); connection = server.local(0);
            try { port = transport.equals("local") ? 0 : server.listen(); } catch (java.io.IOException e) { close(); throw new IllegalStateException("Could not host the table", e); }
            if (port != 0) external = CompletableFuture.supplyAsync(FServerManager::getExternalAddress).exceptionally(ignored -> null);
            forwarding = new PortForwarding(port, port != 0 && autoForward);
        }
        // Connect outside the lobby monitor: the accept worker needs it for dispatch.
        if (transport.equals("network")) try { connection = server.remoteHost(); } catch (java.io.IOException e) { close(); throw new IllegalStateException(e); }
        return state();
    }
    SyncLobby localGuest() {
        if (server == null) throw new IllegalStateException("Host a table first");
        var guest = new SyncLobby(resources, profile); guest.connection = server.joinLocal(); return guest;
    }
    Object join(String address) {
        // Validate and connect before releasing a working seat. A typo must not
        // turn an attempted join into a leave or concession at the current table.
        var next = new SyncTransport.Remote(NetworkInvite.address(address));
        close(); connection = next; return state();
    }
    private synchronized int joinSeat() {
        if (table != null) throw new IllegalStateException("This table has already started");
        for (int i = 1; i < slots.size(); i++) if (!slots.get(i).occupied) {
            slots.get(i).occupied = true; slots.get(i).name = "Player " + (i + 1);
            conversation.join(i, slots.get(i).name); return i;
        }
        throw new IllegalStateException("The table is full");
    }
    private Object request(String method, JsonObject args) {
        var current = connection;
        if (current == null) throw new IllegalStateException("Join or host a lobby first.");
        return current.request(method, args);
    }
    Object configure(String format, int count) { return request("configure", object(Map.of("format", format, "count", count))); }
    Object selectDeck(Deck deck, String format) {
        var entries = new ArrayList<Map<String, Object>>();
        for (var section : deck) for (var card : section.getValue()) entries.add(Map.of("section", section.getKey().name(), "cardId", CardCatalog.describe(card.getKey()).id(), "quantity", card.getValue()));
        return request("deck", object(Map.of("format", format, "name", deck.getName(), "cards", entries)));
    }
    Object ready(boolean value) { return request("ready", object(Map.of("ready", value))); }
    Object social(JsonObject args) { return request("social", args); }
    Object chat(JsonObject args) { return request("chat", args); }
    Object rename(JsonObject args) { return request("name", args); }
    Object start() { return request("start", new JsonObject()); }
    Object returnToLobby() { return request("return", new JsonObject()); }
    Object state() { return connection == null ? idle() : request("lobby", new JsonObject()); }
    Object reconnect() { if (connection instanceof SyncTransport.Remote remote) remote.reconnect(); return state(); }
    MatchEndpoint match() { return new SeatConnection(connection); }
    boolean connected() { return connection != null; }
    synchronized Object testViews() {
        if (!Boolean.getBoolean("mana.test") || server == null || table == null) throw new IllegalStateException("Seat audit is available only to an isolated test host");
        return table.testViews();
    }
    boolean hasMatch() { return connection != null && Boolean.TRUE.equals(((Map<?, ?>)state()).get("matchActive")); }
    Object close() {
        var current = connection; connection = null;
        if (current != null) { if (server == null) try { current.request("leave", new JsonObject()); } catch (Exception ignored) { } current.close(); }
        if (server != null) { server.close(); server = null; }
        if (forwarding != null) { forwarding.close(); forwarding = null; }
        synchronized (this) { if (table != null) { table.close(); table = null; } slots.clear(); external = CompletableFuture.completedFuture(null); }
        return idle();
    }
    private synchronized Object dispatch(int seat, String method, JsonObject p) {
        if (seat < 0 || seat >= slots.size() || !slots.get(seat).occupied) throw new IllegalStateException("This seat is no longer active");
        Slot slot = slots.get(seat);
        switch (method) {
            case "social": requireConversation(p); return conversation.snapshot(seat);
            case "chat": requireConversation(p); conversation.send(seat, p.get("kind").getAsString(), p.get("text").getAsString()); return conversation.snapshot(seat);
            case "name": requireConversation(p); requireSetup(); slot.name = conversation.rename(seat, p.get("name").getAsString()); return conversation.snapshot(seat);
            case "lobby": return lobby(seat);
            case "observe": return table == null ? null : table.seat(seat).state();
            case "reply": requireMatch(); return table.seat(seat).action(p);
            case "concede": requireMatch(); return table.seat(seat).concede(p);
            case "configure": requireHost(seat); requireSetup(); configureSeats(p.get("format").getAsString(), p.get("count").getAsInt()); break;
            case "deck": {
                requireSetup();
                if (!format.equals(p.get("format").getAsString())) throw new IllegalArgumentException("Choose a saved " + format + " deck for this lobby.");
                String name = p.get("name").getAsString();
                if (name.isBlank() || name.length() > 200) throw new IllegalArgumentException("Invalid deck name");
                if (p.getAsJsonArray("cards").size() > 1000) throw new IllegalArgumentException("Deck has too many entries");
                var editor = new DeckEditor(catalog, new Deck(name));
                editor.apply(0, List.of(SyncTransport.JSON.fromJson(p.get("cards"), DeckEditor.Edit[].class)));
                var deck = editor.toDeck();
                var problem = (format.equals("Commander") ? DeckFormat.Commander : DeckFormat.Constructed).getDeckConformanceProblem(deck);
                if (problem != null) throw new IllegalArgumentException(name + " is not legal for " + format + ": " + problem);
                slot.deck = deck; slot.ready = false; conversation.activity(seat, "selected " + deck.getName() + "."); break;
            }
            case "ready": {
                requireSetup(); boolean ready = p.get("ready").getAsBoolean();
                if (ready && slot.deck == null) throw new IllegalStateException("Choose a deck first");
                if (slot.ready != ready) conversation.activity(seat, ready ? "is ready." : "is no longer ready.");
                slot.ready = ready; break;
            }
            case "start": {
                requireHost(seat); requireSetup(); String problem = startProblem(); if (problem != null) throw new IllegalStateException(problem);
                List<RegisteredPlayer> players = slots.stream().map(s -> MatchTable.human(s.deck, format, s.name)).toList();
                table = new MatchTable(format, players, resources, profile); table.start(); conversation.activity(seat, "started the game."); break;
            }
            case "return":
                if (table != null) { if (!table.game.isGameOver()) throw new IllegalStateException("The match is still active"); table.close(); table = null; for (var s : slots) s.ready = false; conversation.activity(seat, "returned the table to the lobby."); }
                slot.ready = false; break;
            case "leave":
                if (table != null && !table.game.isGameOver()) table.seat(seat).concede(object(Map.of("sessionId", table.id)));
                conversation.leave(seat); slot.occupied = false; slot.ready = false; slot.deck = null; server.revoke(seat); break;
            default: throw new IllegalArgumentException("Unknown synchronization command");
        }
        return lobby(seat);
    }
    private void requireHost(int seat) { if (seat != 0) throw new IllegalStateException("Only the host can change this table"); }
    private void requireConversation(JsonObject p) {
        if (!p.has("tableId") || !conversation.id().equals(p.get("tableId").getAsString())) throw new IllegalStateException("This table has changed. Reopen chat.");
    }
    private void requireSetup() { if (table != null) throw new IllegalStateException("Return to the lobby before changing the table"); }
    private void requireMatch() { if (table == null) throw new IllegalStateException("No active match"); }
    private void configureSeats(String nextFormat, int count) {
        if (!List.of("Commander", "Constructed").contains(nextFormat)) throw new IllegalArgumentException("Unsupported format");
        if (count < 2 || count > (nextFormat.equals("Commander") ? 6 : 2)) throw new IllegalArgumentException("Invalid number of seats");
        for (int i = count; i < slots.size(); i++) if (slots.get(i).occupied) throw new IllegalStateException("A connected player occupies a seat you are trying to remove");
        while (slots.size() > count) slots.remove(slots.size() - 1);
        while (slots.size() < count) slots.add(new Slot("Player " + (slots.size() + 1), false));
        for (var slot : slots) { slot.ready = false; if (!format.equals(nextFormat)) slot.deck = null; }
        format = nextFormat;
    }
    private String startProblem() {
        if (table != null) return "A game is already running.";
        long occupied = slots.stream().filter(s -> s.occupied).count();
        if (occupied < slots.size()) return "Waiting for players (" + occupied + "/" + slots.size() + " connected). Invite more friends or change the number of seats.";
        for (var slot : slots) if (slot.deck == null || !slot.ready) return slot.name + " needs to choose a deck and ready up.";
        return null;
    }
    private Map<String, Object> lobby(int viewer) {
        var result = idle();
        result.put("tableId", conversation.id());
        result.put("mode", viewer == 0 ? "hosting" : "joined"); result.put("status", table == null ? "Connected to table." : "Game in progress.");
        result.put("hosting", viewer == 0); result.put("format", format); result.put("playerCount", slots.size()); result.put("maxPlayers", format.equals("Commander") ? 6 : 2);
        result.put("canStart", viewer == 0 && startProblem() == null); result.put("startProblem", viewer == 0 ? startProblem() : "Only the host can start the game."); result.put("matchActive", table != null);
        var rows = new ArrayList<Map<String, Object>>();
        for (int i = 0; i < slots.size(); i++) {
            var slot = slots.get(i); var row = new LinkedHashMap<String, Object>();
            row.put("index", i); row.put("name", slot.name); row.put("type", !slot.occupied ? "OPEN" : i == viewer ? "LOCAL" : "REMOTE");
            row.put("ready", slot.ready); row.put("local", i == viewer); row.put("team", i); row.put("deck", slot.deck == null ? null : slot.deck.getName()); rows.add(row);
        }
        result.put("slots", rows);
        if (viewer == 0) {
            result.put("addresses", addresses()); result.put("internetInvite", NetworkInvite.encode(external.getNow(null), port));
            result.put("addressLookupPending", !external.isDone()); result.put("portMapping", forwarding == null ? "disabled" : forwarding.status());
        }
        return result;
    }
    private List<Map<String, Object>> addresses() {
        var addresses = new LinkedHashMap<>(FServerManager.getAllLocalAddresses()); addresses.putIfAbsent("This computer", "127.0.0.1");
        String internet = external.getNow(null); if (NetworkInvite.encode(internet, port) != null) addresses.put("Internet", internet.trim());
        var result = new ArrayList<Map<String, Object>>();
        for (var address : addresses.entrySet()) result.add(Map.of("label", address.getKey(), "url", address.getValue() + ":" + port,
                "invite", Objects.requireNonNullElse(NetworkInvite.encode(address.getValue(), port), ""), "preferred", address.getKey().equals("Internet")));
        return result;
    }
    private static JsonObject object(Object value) { return SyncTransport.JSON.toJsonTree(value).getAsJsonObject(); }
    private static LinkedHashMap<String, Object> idle() {
        var state = new LinkedHashMap<String, Object>();
        state.put("mode", "idle"); state.put("status", "Not connected."); state.put("error", null); state.put("canStart", false); state.put("startProblem", "Host a table first.");
        state.put("hosting", false); state.put("format", "Constructed"); state.put("playerCount", 2); state.put("maxPlayers", 2); state.put("addressLookupPending", false);
        state.put("matchActive", false); state.put("portMapping", "disabled"); state.put("internetInvite", null); state.put("addresses", List.of()); state.put("slots", List.of()); state.put("messages", List.of()); return state;
    }
}
